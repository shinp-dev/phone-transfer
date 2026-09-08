using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Domain;

namespace PhoneTransfer.Infrastructure.Storage;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsShareFileSystem
{
    public IDurableShareSession OpenDurable(ShareConfiguration configuration) => new Session(configuration.RootPath);

    private sealed partial class Session
    {
        private const string DurablePrefix = PrivatePrefix + "durable-";
        private readonly HashSet<DurableFile> durableFiles = [];
        public string RootIdentity { get { lock (gate) { CheckOpen(); return WindowsFileNative.Inspect(Root).Identity; } } }

        public IDurableStagingFile CreateDurable(RelativeSharePath destination)
        {
            lock (gate)
            {
                CheckOpen();
                if (durableFiles.Count >= MaxOpenFiles) throw new IOException("TOO_MANY_OPEN_FILES");
                var parts = Components(destination, requireFile: true);
                var chain = Traverse(parts[..^1]);
                SafeFileHandle? directory = null;
                SafeFileHandle? file = null;
                try
                {
                    var directoryToken = Guid.NewGuid();
                    var fileToken = Guid.NewGuid();
                    directory = OpenVerified(Root, DurablePrefix + directoryToken.ToString("N"), true,
                        create: true, delete: true, privateAcl: true);
                    WindowsFileNative.ValidatePrivateAcl(directory);
                    file = OpenVerified(directory, fileToken.ToString("N") + ".part", false,
                        create: true, writable: true, delete: true, privateAcl: true);
                    WindowsFileNative.ValidatePrivateAcl(file);
                    RandomAccess.FlushToDisk(file);
                    var capability = new StagingCapability(directoryToken, fileToken,
                        WindowsFileNative.Inspect(directory).Identity, WindowsFileNative.Inspect(file).Identity,
                        RootIdentity, WindowsFileNative.Inspect(chain.Last(Root)).Identity);
                    var opened = new DurableFile(this, file, directory, chain, parts[^1], capability);
                    durableFiles.Add(opened);
                    return opened;
                }
                catch
                {
                    // Orphans are reconciled by bounded handle-relative startup cleanup.
                    file?.Dispose();
                    directory?.Dispose();
                    chain.Dispose();
                    throw;
                }
            }
        }

        public IDurableStagingFile Reopen(StagingCapability capability, RelativeSharePath destination)
        {
            lock (gate)
            {
                CheckOpen();
                if (capability.DirectoryToken == Guid.Empty || capability.FileToken == Guid.Empty ||
                    RootIdentity != capability.RootIdentity) throw new IOException("STAGING_IDENTITY_MISMATCH");
                if (durableFiles.Count >= MaxOpenFiles) throw new IOException("TOO_MANY_OPEN_FILES");
                var parts = Components(destination, requireFile: true);
                var chain = Traverse(parts[..^1]);
                SafeFileHandle? directory = null;
                SafeFileHandle? file = null;
                try
                {
                    if (WindowsFileNative.Inspect(chain.Last(Root)).Identity != capability.DestinationParentIdentity)
                        throw new IOException("DESTINATION_PARENT_CHANGED");
                    directory = OpenVerified(Root, DurablePrefix + capability.DirectoryToken.ToString("N"), true,
                        delete: true, privateAcl: true);
                    WindowsFileNative.ValidatePrivateAcl(directory);
                    if (WindowsFileNative.Inspect(directory).Identity != capability.DirectoryIdentity)
                        throw new IOException("STAGING_DIRECTORY_CHANGED");
                    file = OpenVerified(directory, capability.FileToken.ToString("N") + ".part", false,
                        writable: true, delete: true, privateAcl: true);
                    WindowsFileNative.ValidatePrivateAcl(file);
                    if (WindowsFileNative.Inspect(file).Identity != capability.FileIdentity)
                        throw new IOException("STAGING_FILE_CHANGED");
                    var opened = new DurableFile(this, file, directory, chain, parts[^1], capability);
                    durableFiles.Add(opened);
                    return opened;
                }
                catch { file?.Dispose(); directory?.Dispose(); chain.Dispose(); throw; }
            }
        }

        public bool VerifyDestination(StagingCapability capability, RelativeSharePath destination, long size, string sha256)
        {
            lock (gate)
            {
                CheckOpen();
                if (RootIdentity != capability.RootIdentity) return false;
                var parts = Components(destination, requireFile: true);
                using var chain = Traverse(parts[..^1]);
                if (WindowsFileNative.Inspect(chain.Last(Root)).Identity != capability.DestinationParentIdentity) return false;
                using var file = OpenVerified(chain.Last(Root), parts[^1], false);
                var info = WindowsFileNative.Inspect(file);
                return info.Identity == capability.FileIdentity && info.Length == size && DigestMatches(file, size, sha256);
            }
        }

        private static bool DigestMatches(SafeFileHandle file, long size, string expected)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long offset = 0;
            while (offset < size)
            {
                var count = RandomAccess.Read(file, buffer.AsSpan(0, (int)Math.Min(buffer.Length, size - offset)), offset);
                if (count <= 0) throw new IOException("FILE_ENDED_EARLY");
                hash.AppendData(buffer, 0, count);
                offset += count;
            }
            return CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expected));
        }

        public void CleanupOrphans(IReadOnlySet<Guid> referencedDirectories)
        {
            lock (gate)
            {
                CheckOpen();
                var names = WindowsFileNative.Enumerate(Root, 4097);
                if (names.Count >= 4097) throw new IOException("RECOVERY_SCAN_LIMIT");
                foreach (var name in names)
                {
                    if (!name.StartsWith(DurablePrefix, StringComparison.Ordinal) ||
                        !Guid.TryParseExact(name[DurablePrefix.Length..], "N", out var token) || referencedDirectories.Contains(token)) continue;
                    try
                    {
                        using var directory = OpenVerified(Root, name, true, delete: true, privateAcl: true);
                        WindowsFileNative.ValidatePrivateAcl(directory);
                        var children = WindowsFileNative.Enumerate(directory, 2);
                        if (children.Count > 1) continue; // Unknown layout is quarantined, never recursively traversed.
                        if (children.Count == 1)
                        {
                            var child = children[0];
                            if (!child.EndsWith(".part", StringComparison.Ordinal) || !Guid.TryParseExact(child[..^5], "N", out _)) continue;
                            using var file = OpenVerified(directory, child, false, delete: true, privateAcl: true);
                            WindowsFileNative.ValidatePrivateAcl(file);
                            WindowsFileNative.DeleteOnClose(file);
                        }
                        WindowsFileNative.DeleteOnClose(directory);
                    }
                    catch (IOException) { /* Unsafe, busy or changed objects stay hidden and untouched. */ }
                }
            }
        }

        public void CleanupEmptyDirectory(StagingCapability capability)
        {
            lock (gate)
            {
                CheckOpen();
                if (RootIdentity != capability.RootIdentity) return;
                using var directory = OpenVerified(Root, DurablePrefix + capability.DirectoryToken.ToString("N"), true,
                    delete: true, privateAcl: true);
                WindowsFileNative.ValidatePrivateAcl(directory);
                if (WindowsFileNative.Inspect(directory).Identity != capability.DirectoryIdentity) return;
                if (WindowsFileNative.Enumerate(directory, 1).Count == 0) WindowsFileNative.DeleteOnClose(directory);
            }
        }

        private sealed class DurableFile(Session owner, SafeFileHandle handle, SafeFileHandle directory,
            Chain chain, string destination, StagingCapability capability) : IDurableStagingFile
        {
            private bool closed;
            private bool renamed;
            public StagingCapability Capability { get; } = capability;
            private void Check()
            {
                owner.CheckOpen();
                ObjectDisposedException.ThrowIf(closed, this);
                if (renamed) throw new InvalidOperationException("STAGING_ALREADY_COMMITTED");
                if (WindowsFileNative.Inspect(handle).Identity != Capability.FileIdentity ||
                    WindowsFileNative.Inspect(directory).Identity != Capability.DirectoryIdentity ||
                    owner.RootIdentity != Capability.RootIdentity ||
                    WindowsFileNative.Inspect(chain.Last(owner.Root)).Identity != Capability.DestinationParentIdentity)
                    throw new IOException("STAGING_IDENTITY_MISMATCH");
            }
            public long Length { get { lock (owner.gate) { Check(); return RandomAccess.GetLength(handle); } } }
            public int Read(long offset, Span<byte> buffer)
            {
                lock (owner.gate) { Check(); return RandomAccess.Read(handle, buffer, offset); }
            }
            public void Write(long offset, ReadOnlySpan<byte> buffer)
            {
                lock (owner.gate) { Check(); RandomAccess.Write(handle, buffer, offset); }
            }
            public void Flush()
            {
                lock (owner.gate) { Check(); RandomAccess.FlushToDisk(handle); }
            }
            public void Truncate(long committedOffset)
            {
                lock (owner.gate)
                {
                    Check();
                    if (committedOffset < 0 || RandomAccess.GetLength(handle) < committedOffset) throw new IOException("COMMITTED_PREFIX_MISSING");
                    RandomAccess.SetLength(handle, committedOffset);
                    RandomAccess.FlushToDisk(handle);
                    if (RandomAccess.GetLength(handle) != committedOffset) throw new IOException("TRUNCATE_FAILED");
                }
            }
            public void CompleteNoReplace()
            {
                lock (owner.gate)
                {
                    Check();
                    WindowsFileNative.RenameNoReplace(handle, chain.Last(owner.Root), destination);
                    renamed = true; // No fallible cleanup after the commit primitive.
                }
            }
            public void Delete()
            {
                lock (owner.gate)
                {
                    Check();
                    WindowsFileNative.DeleteOnClose(handle);
                    handle.Dispose();
                    try { WindowsFileNative.DeleteOnClose(directory); }
                    finally { Dispose(); }
                }
            }
            public void Dispose()
            {
                lock (owner.gate)
                {
                    if (closed) return;
                    closed = true;
                    handle.Dispose();
                    directory.Dispose();
                    chain.Dispose();
                    owner.durableFiles.Remove(this);
                }
            }
        }
    }
}
