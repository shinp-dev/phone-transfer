using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Domain;

namespace PhoneTransfer.Infrastructure.Storage;

[SupportedOSPlatform("windows")]
public sealed class WindowsShareFileSystem : IShareFileSystem
{
    public IShareFileSession Open(ShareConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new Session(configuration.RootPath);
    }

    private sealed class Session : IShareFileSession
    {
        private const string PrivatePrefix = ".phone-transfer-staging-";
        private const int MaxOpenFiles = 64;
        private readonly object gate = new();
        private readonly List<SafeFileHandle> rootChain = [];
        private readonly HashSet<OpenFile> files = [];
        private SafeFileHandle Root => rootChain[^1];
        private SafeFileHandle? stagingDirectory;
        private bool disposed;

        internal Session(string rootPath)
        {
            // Parse local configuration strictly; do not normalize away unsafe components.
            if (string.IsNullOrEmpty(rootPath) || rootPath.Length < 4 || !char.IsAsciiLetter(rootPath[0])
                || rootPath[1] != ':' || rootPath[2] != '\\')
                throw new ArgumentException("LOCAL_ABSOLUTE_SHARE_ROOT_REQUIRED", nameof(rootPath));
            var relative = RelativeSharePath.Parse(rootPath[3..].TrimEnd('\\').Replace('\\', '/'));
            var segments = Components(relative);
            try
            {
                var volume = WindowsFileNative.OpenVolume(rootPath[..3]);
                rootChain.Add(volume);
                var info = WindowsFileNative.Inspect(volume);
                WindowsFileNative.ValidateVolume(volume, info.Path);
                foreach (var segment in segments)
                    rootChain.Add(OpenVerified(Root, segment, true));
            }
            catch
            {
                CloseReverse(rootChain);
                throw;
            }
        }

        public IReadOnlyList<ShareEntry> List(RelativeSharePath directory, int limit = 200)
        {
            if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
            lock (gate)
            {
                CheckOpen();
                using var chain = Traverse(Components(directory));
                WindowsFileNative.Inspect(chain.Last(Root));
                var entries = new List<ShareEntry>();
                // Bounded work, including names that cannot safely be exposed. Pagination is future work.
                foreach (var name in WindowsFileNative.Enumerate(chain.Last(Root), 4096))
                {
                    try
                    {
                        Components(RelativeSharePath.Parse(name));
                        using var child = OpenVerified(chain.Last(Root), name, null);
                        entries.Add(new ShareEntry(name, WindowsFileNative.Inspect(child).Directory));
                    }
                    catch (ArgumentException) { continue; }
                    catch (IOException) { continue; } // Unsafe, disappeared, or locked entry: never expose it.
                    if (entries.Count == limit) break;
                }
                return entries;
            }
        }

        public IShareReadFile OpenRead(RelativeSharePath file)
        {
            lock (gate)
            {
                CheckCapacity();
                var parts = Components(file, requireFile: true);
                var chain = Traverse(parts[..^1]);
                try
                {
                    var handle = OpenVerified(chain.Last(Root), parts[^1], false);
                    var opened = new OpenFile(this, handle, chain, null);
                    files.Add(opened);
                    return opened;
                }
                catch { chain.Dispose(); throw; }
            }
        }

        public IShareStagingFile CreateStaging(RelativeSharePath destination)
        {
            lock (gate)
            {
                CheckCapacity();
                var parts = Components(destination, requireFile: true);
                var chain = Traverse(parts[..^1]);
                try
                {
                    // Never adopt an existing private directory, even with the same name.
                    stagingDirectory ??= OpenVerified(Root, PrivatePrefix + Guid.NewGuid().ToString("N"),
                        true, create: true, delete: true, privateAcl: true);
                    var handle = OpenVerified(stagingDirectory, Guid.NewGuid().ToString("N") + ".part",
                        false, create: true, writable: true, delete: true, privateAcl: true);
                    var opened = new OpenFile(this, handle, chain, parts[^1]);
                    files.Add(opened);
                    return opened;
                }
                catch { chain.Dispose(); throw; }
            }
        }

        private static string[] Components(RelativeSharePath path, bool requireFile = false)
        {
            ArgumentNullException.ThrowIfNull(path);
            // Keep the Domain parser as the syntax boundary, independently of handle containment.
            RelativeSharePath.Parse(path.Value, allowRoot: !requireFile);
            var result = path.Value.Length == 0 ? [] : path.Value.Split('/');
            if (result.Any(IsPrivate)) throw new ArgumentException("PRIVATE_PATH_REJECTED", nameof(path));
            return result;
        }

        private static bool IsPrivate(string name) => name.StartsWith(PrivatePrefix, StringComparison.OrdinalIgnoreCase);

        private static SafeFileHandle OpenVerified(SafeFileHandle parent, string name, bool? directory,
            bool create = false, bool writable = false, bool delete = false, bool privateAcl = false)
        {
            var parentInfo = WindowsFileNative.Inspect(parent);
            var child = WindowsFileNative.OpenChild(parent, name, directory, create, writable, delete, privateAcl);
            try
            {
                var info = WindowsFileNative.Inspect(child);
                var prefix = parentInfo.Path.EndsWith('\\') ? parentInfo.Path : parentInfo.Path + '\\';
                // Additional handle-derived check, not the containment mechanism on its own.
                // Only one direct child on the same volume, including case-sensitive directories.
                if (info.Volume != parentInfo.Volume || !info.Path.StartsWith(prefix, StringComparison.Ordinal)
                    || info.Path[prefix.Length..].Contains('\\') || info.Path.Length == prefix.Length
                    || (directory.HasValue && directory.Value != info.Directory))
                    throw new IOException("HANDLE_CONTAINMENT_FAILED");
                // Reject aliases (including 8.3 names) of private directories after opening, too.
                if (!privateAcl && IsPrivate(info.Path[prefix.Length..]))
                    throw new IOException("PRIVATE_PATH_REJECTED");
                return child;
            }
            catch
            {
                // A newly created object is ours; cleanup by handle, never by a reconstructed path.
                try { if (create) WindowsFileNative.DeleteOnClose(child); }
                finally { child.Dispose(); }
                throw;
            }
        }

        private Chain Traverse(string[] segments)
        {
            var chain = new Chain();
            try
            {
                foreach (var segment in segments) chain.Handles.Add(OpenVerified(chain.Last(Root), segment, true));
                return chain;
            }
            catch { chain.Dispose(); throw; }
        }

        private void CheckOpen() => ObjectDisposedException.ThrowIf(disposed, this);

        private void CheckCapacity()
        {
            CheckOpen();
            if (files.Count >= MaxOpenFiles) throw new IOException("TOO_MANY_OPEN_FILES");
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                Exception? error = null;
                foreach (var file in files.ToArray())
                {
                    try { file.Dispose(); }
                    catch (IOException exception) { error ??= exception; }
                }
                try
                {
                    if (stagingDirectory is not null) WindowsFileNative.DeleteOnClose(stagingDirectory);
                }
                catch (IOException exception) { error ??= exception; }
                finally
                {
                    stagingDirectory?.Dispose();
                    CloseReverse(rootChain);
                }
                if (error is not null) throw new IOException("PRIVATE_STAGING_CLEANUP_FAILED", error);
            }
        }

        private static void CloseReverse(List<SafeFileHandle> handles)
        {
            for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
            handles.Clear();
        }

        private sealed class Chain : IDisposable
        {
            internal List<SafeFileHandle> Handles { get; } = [];
            internal SafeFileHandle Last(SafeFileHandle root) => Handles.Count == 0 ? root : Handles[^1];
            public void Dispose() => CloseReverse(Handles);
        }

        // Sole owner of its file handle and traversal chain. The session owns every live child.
        // The common gate prevents I/O/disposal races and no raw handle or FileStream is exposed.
        private sealed class OpenFile(Session owner, SafeFileHandle handle, Chain chain, string? destination) : IShareStagingFile
        {
            private bool closed;
            private bool completed;

            private void CheckOpen()
            {
                owner.CheckOpen();
                ObjectDisposedException.ThrowIf(closed, this);
            }

            private void CheckStaging()
            {
                CheckOpen();
                if (destination is null) throw new InvalidOperationException("READ_ONLY_FILE");
            }

            public long Length
            {
                get { lock (owner.gate) { CheckOpen(); return RandomAccess.GetLength(handle); } }
            }

            public int Read(long offset, Span<byte> buffer)
            {
                lock (owner.gate) { CheckOpen(); return RandomAccess.Read(handle, buffer, offset); }
            }

            public void Write(long offset, ReadOnlySpan<byte> buffer)
            {
                lock (owner.gate) { CheckStaging(); RandomAccess.Write(handle, buffer, offset); }
            }

            public void Flush()
            {
                lock (owner.gate) { CheckStaging(); RandomAccess.FlushToDisk(handle); }
            }

            public bool DestinationExists()
            {
                lock (owner.gate)
                {
                    CheckStaging();
                    try
                    {
                        using var existing = OpenVerified(chain.Last(owner.Root), destination!, null);
                        return true;
                    }
                    catch (IOException exception) when (WindowsFileNative.IsMissing(exception)) { return false; }
                }
            }

            public void CompleteNoReplace()
            {
                lock (owner.gate)
                {
                    CheckStaging();
                    RandomAccess.FlushToDisk(handle);
                    WindowsFileNative.Inspect(handle);
                    WindowsFileNative.Inspect(chain.Last(owner.Root));
                    WindowsFileNative.RenameNoReplace(handle, chain.Last(owner.Root), destination!);
                    completed = true;
                    Dispose();
                }
            }

            public void Dispose()
            {
                lock (owner.gate)
                {
                    if (closed) return;
                    closed = true;
                    try
                    {
                        if (destination is not null && !completed) WindowsFileNative.DeleteOnClose(handle);
                    }
                    finally
                    {
                        handle.Dispose();
                        chain.Dispose();
                        owner.files.Remove(this);
                    }
                }
            }
        }
    }
}
