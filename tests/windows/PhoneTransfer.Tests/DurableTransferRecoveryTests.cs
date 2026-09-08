using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Domain;
using PhoneTransfer.Infrastructure.Persistence;
using Xunit;

namespace PhoneTransfer.Tests;

public sealed class DurableTransferRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PhoneTransferDurable", Guid.NewGuid().ToString("N"));
    private readonly MemoryFiles files = new();
    private readonly Configuration store = new();
    private readonly byte[] payload = "durable prefix and tail"u8.ToArray();
    private readonly Guid key = Guid.NewGuid();
    private PairedDevice device = new(Guid.NewGuid(), "phone", new string('a', 64), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DevicePermissions.All, false);
    private DurableFileTransferService? service;
    private SqliteTransferJournal? journal;
    private Action<TransferFaultPoint>? fault;
    private Func<ITransferJournal, ITransferJournal>? wrapJournal;
    private string Database => Path.Combine(directory, "transfers.db");

    private DurableFileTransferService Start()
    {
        service?.Dispose();
        journal = new SqliteTransferJournal(Database);
        service = new DurableFileTransferService(store, files, files, wrapJournal?.Invoke(journal) ?? journal,
            id => id == device.DeviceId ? device : null, point => fault?.Invoke(point));
        service.Initialize();
        return service;
    }
    private TransferSnapshot Create() => service!.CreateTransfer(device, store.Value!.Generation,
        RelativeSharePath.Parse("final.bin"), payload.Length, Convert.ToHexStringLower(SHA256.HashData(payload)), key);
    private void CrashAt(TransferFaultPoint point) => fault = actual => { if (actual == point) throw new SimulatedCrash(); };

    [Fact]
    public void CreateIdempotencySurvivesRestartAndRejectsDifferentMetadata()
    {
        Start();
        var first = Create();
        Start();
        Assert.Equal(first.TransferId, Create().TransferId);
        var error = Assert.Throws<BasicFileTransferException>(() => service!.CreateTransfer(device, store.Value!.Generation,
            RelativeSharePath.Parse("other.bin"), payload.Length, first.Sha256, key));
        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
        Assert.Single(journal!.Load());
    }

    [Theory]
    [InlineData(TransferFaultPoint.StagingCreated, false)]
    [InlineData(TransferFaultPoint.TransferCreated, true)]
    public void CreateCrashLeavesEitherAnOrphanOrOneDurableIdempotentRecord(TransferFaultPoint point, bool committed)
    {
        Start();
        CrashAt(point);
        Assert.Throws<SimulatedCrash>(() => Create());
        fault = null;
        Start();
        Assert.Equal(committed ? 1 : 0, journal!.Load().Count);
        Assert.Equal(committed ? 1 : 0, files.Staging.Count);
        var created = Create();
        Assert.Equal(created.TransferId, Create().TransferId);
    }

    [Theory]
    [InlineData(TransferFaultPoint.ChunkWritten, false)]
    [InlineData(TransferFaultPoint.ChunkFlushed, false)]
    [InlineData(TransferFaultPoint.OffsetCommitted, true)]
    public void CommittedOffsetIsAuthorityAndUncommittedTailIsTruncatedAcrossTwoRestarts(TransferFaultPoint point, bool committed)
    {
        Start();
        var created = Create();
        CrashAt(point);
        Assert.Throws<SimulatedCrash>(() => service!.Append(device, created.TransferId, 0, payload));
        fault = null;
        for (var restart = 0; restart < 2; restart++)
        {
            Start();
            var expected = committed ? payload.Length : 0;
            Assert.Equal(expected, service!.GetTransfer(device, created.TransferId).TransferredBytes);
            Assert.Equal(expected, Assert.Single(files.Staging.Values).Bytes.Length);
        }
    }

    [Theory]
    [InlineData(TransferFaultPoint.VerifyingCommitted)]
    [InlineData(TransferFaultPoint.HashVerified)]
    [InlineData(TransferFaultPoint.Renamed)]
    [InlineData(TransferFaultPoint.CompletedCommitted)]
    public void CompletionCrashIncludingRenameBeforeDbConvergesToOneCompletedFile(TransferFaultPoint point)
    {
        Start();
        var created = Create();
        service!.Append(device, created.TransferId, 0, payload);
        CrashAt(point);
        Assert.Throws<SimulatedCrash>(() => service.Complete(device, created.TransferId));
        fault = null;
        for (var restart = 0; restart < 2; restart++)
        {
            Start();
            Assert.Equal(TransferState.Completed, service!.GetTransfer(device, created.TransferId).State);
            Assert.Equal(payload, files.Destination!.Bytes);
        }
        Assert.Equal(1, files.Renames);
    }

    [Fact]
    public void TotalBytesCommittedBeforeVerifyRemainCompletableAfterRestart()
    {
        Start();
        var created = Create();
        service!.Append(device, created.TransferId, 0, payload);
        Start();
        Assert.Equal(TransferState.Completed, service!.Complete(device, created.TransferId).State);
    }

    [Fact]
    public void DestinationImpostorDoesNotBecomeCompletedOrGetOverwritten()
    {
        Start();
        var created = Create();
        service!.Append(device, created.TransferId, 0, payload);
        CrashAt(TransferFaultPoint.Renamed);
        Assert.Throws<SimulatedCrash>(() => service.Complete(device, created.TransferId));
        files.Destination = new MemoryObject(NewCapability(), payload.ToArray());
        fault = null;
        Start();
        Assert.Equal(TransferState.Failed, service!.GetTransfer(device, created.TransferId).State);
        Assert.Equal(payload, files.Destination.Bytes);
        Assert.Equal(1, files.Renames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedOffsetAheadOfFileOrIdentityMismatchFailsClosed(bool mismatch)
    {
        Start();
        var created = Create();
        service!.Append(device, created.TransferId, 0, payload);
        var staged = Assert.Single(files.Staging.Values);
        if (mismatch) staged.Capability = staged.Capability with { FileIdentity = new string('f', 48) };
        else staged.Bytes = [];
        Start();
        Assert.Equal(TransferState.Failed, service!.GetTransfer(device, created.TransferId).State);
        Assert.Throws<BasicFileTransferException>(() => service.Append(device, created.TransferId, 0, payload));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DiskFullPartialWriteOrFlushFailureNeverAdvancesDurableOffset(bool flushFailure, bool truncateFailure)
    {
        Start();
        var created = Create();
        service!.Append(device, created.TransferId, 0, payload.AsMemory(0, 4));
        files.PartialWriteFailure = !flushFailure;
        files.FlushFailure = flushFailure;
        files.TruncateFailure = truncateFailure;
        var error = Assert.Throws<BasicFileTransferException>(() => service.Append(device, created.TransferId, 4, payload.AsMemory(4)));
        var record = Assert.Single(journal!.Load());
        Assert.Equal(4, record.CommittedOffset);
        Assert.Equal(!truncateFailure, error.Retryable);
        Assert.Equal(truncateFailure ? TransferState.Failed : TransferState.Paused, record.State);
        if (!truncateFailure) Assert.Equal(4, Assert.Single(files.Staging.Values).Bytes.Length);
        files.PartialWriteFailure = files.FlushFailure = files.TruncateFailure = false;
        Start();
        if (!truncateFailure)
        {
            service!.Append(device, created.TransferId, 4, payload.AsMemory(4));
            Assert.Equal(TransferState.Completed, service.Complete(device, created.TransferId).State);
        }
        else Assert.Equal(TransferState.Failed, service!.GetTransfer(device, created.TransferId).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelSurvivesCrashAndCleanupFailureNeverResurrectsTerminalTransfer(bool cleanupFailure)
    {
        Start();
        var created = Create();
        files.CleanupFailure = cleanupFailure;
        CrashAt(TransferFaultPoint.CancelCommitted);
        Assert.Throws<SimulatedCrash>(() => service!.Cancel(device, created.TransferId));
        fault = null;
        for (var restart = 0; restart < 2; restart++)
        {
            Start();
            Assert.Equal(TransferState.Cancelled, service!.GetTransfer(device, created.TransferId).State);
            Assert.Throws<BasicFileTransferException>(() => service.Append(device, created.TransferId, 0, payload));
        }
    }

    [Fact]
    public void RevokeCommitBeforeCleanupSurvivesRestartAndDeniesStaleAuthorizedRequest()
    {
        Start();
        var created = Create();
        var stale = device;
        CrashAt(TransferFaultPoint.RevokeCommitted);
        Assert.Throws<SimulatedCrash>(() => service!.RevokeDevice(device.DeviceId, () => { device = device with { Revoked = true }; return true; }));
        fault = null;
        Start();
        Assert.Equal(TransferState.Cancelled, Assert.Single(journal!.Load()).State);
        Assert.Throws<BasicFileTransferException>(() => service!.Append(stale, created.TransferId, 0, payload));
    }

    [Fact]
    public void ShareChangePreventsResumeWithoutOpeningOldRoot()
    {
        Start();
        var created = Create();
        store.Value = new ShareConfiguration("new-share", Guid.NewGuid());
        Start();
        Assert.Equal(TransferState.Cancelled, service!.GetTransfer(device, created.TransferId).State);
        Assert.Equal(0, files.Reopens);
        Assert.Single(files.Staging); // Quarantined, not reopened via an old physical path.
    }

    [Fact]
    public void StartupReconciliationCrashAndSecondRestartRemainStable()
    {
        Start();
        var created = Create();
        CrashAt(TransferFaultPoint.ChunkFlushed);
        Assert.Throws<SimulatedCrash>(() => service!.Append(device, created.TransferId, 0, payload));
        CrashAt(TransferFaultPoint.ReconciliationStep);
        Assert.Throws<SimulatedCrash>(() => Start());
        fault = null;
        Start();
        var recovered = service!.GetTransfer(device, created.TransferId);
        Start();
        Assert.Equal(recovered, service!.GetTransfer(device, created.TransferId));
        Assert.Empty(Assert.Single(files.Staging.Values).Bytes);
    }

    [Fact]
    public void FileApiIsUnavailableUntilReconciliationCompletes()
    {
        journal = new SqliteTransferJournal(Database);
        using var uninitialized = new DurableFileTransferService(store, files, files, journal, _ => device);
        Assert.Equal("TRANSFER_SERVICE_UNAVAILABLE", Assert.Throws<BasicFileTransferException>(() => uninitialized.ListShares(device)).Code);
    }

    [Fact]
    public void UnsupportedSchemaAndCorruptionNeverAdoptStaging()
    {
        Start();
        Create();
        service!.Dispose();
        using (var db = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version=999";
            command.ExecuteNonQuery();
        }
        Assert.Throws<TransferJournalException>(() => new SqliteTransferJournal(Database));
        File.WriteAllText(Database, "corrupt SQLite");
        Assert.Throws<TransferJournalException>(() => new SqliteTransferJournal(Database));
        Assert.Single(files.Staging);
        Assert.Equal(0, files.Reopens);
    }

    [Fact]
    public async Task RevokeDuringHashCommitsAuthorizationBeforeTransferCleanupAndPreventsRename()
    {
        Start();
        var created = Create();
        service!.Append(device, created.TransferId, 0, payload);
        using var hashReached = new ManualResetEventSlim();
        using var continueHash = new ManualResetEventSlim();
        using var revokeCommitted = new ManualResetEventSlim();
        fault = point =>
        {
            if (point == TransferFaultPoint.HashVerified) { hashReached.Set(); Assert.True(continueHash.Wait(TimeSpan.FromSeconds(15))); }
            if (point == TransferFaultPoint.RevokeCommitted) revokeCommitted.Set();
        };
        var completion = Task.Run(() => Assert.Throws<BasicFileTransferException>(() => service.Complete(device, created.TransferId)));
        Assert.True(hashReached.Wait(TimeSpan.FromSeconds(15)));
        var revoke = Task.Run(() => service.RevokeDevice(device.DeviceId, () => { device = device with { Revoked = true }; return true; }));
        try { Assert.True(revokeCommitted.Wait(TimeSpan.FromSeconds(15))); }
        finally { continueHash.Set(); }
        await Task.WhenAll(completion, revoke);
        Assert.Equal(TransferState.Cancelled, Assert.Single(journal!.Load()).State);
        Assert.Null(files.Destination);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AmbiguousJournalCommitPoisonsProcessWithoutRollingBackAcknowledgedFileBytes(bool afterCommit, bool completion)
    {
        wrapJournal = inner => new FailingJournal(inner, afterCommit, completion ? TransferState.Completed : TransferState.Transferring);
        Start();
        var created = Create();
        if (completion) service!.Append(device, created.TransferId, 0, payload);
        Assert.Throws<BasicFileTransferException>(() => completion
            ? service!.Complete(device, created.TransferId)
            : service!.Append(device, created.TransferId, 0, payload));
        Assert.Equal("TRANSFER_SERVICE_UNAVAILABLE", Assert.Throws<BasicFileTransferException>(() => service!.GetTransfer(device, created.TransferId)).Code);
        wrapJournal = null;
        Start();
        var recovered = service!.GetTransfer(device, created.TransferId);
        Assert.Equal(completion || afterCommit ? payload.Length : 0, recovered.TransferredBytes);
        if (completion) Assert.Equal(TransferState.Completed, recovered.State);
        else Assert.Equal(recovered.TransferredBytes, Assert.Single(files.Staging.Values).Bytes.Length);
    }

    [Fact]
    public async Task SameTransferPatchesSerializeAndAnUnrelatedTransferProgressesDuringHash()
    {
        Start();
        var first = Create();
        var other = service!.CreateTransfer(device, store.Value!.Generation, RelativeSharePath.Parse("other.bin"), payload.Length,
            first.Sha256, Guid.NewGuid());
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fault = point =>
        {
            if (point == TransferFaultPoint.ChunkFlushed) { reached.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); }
        };
        var append = Task.Run(() => service.Append(device, first.TransferId, 0, payload));
        Assert.True(reached.Wait(TimeSpan.FromSeconds(15)));
        var duplicate = Task.Run(() => Assert.Throws<BasicFileTransferException>(() => service.Append(device, first.TransferId, 0, payload)));
        release.Set();
        await Task.WhenAll(append, duplicate);
        var duplicateError = await duplicate;
        Assert.Equal("OFFSET_MISMATCH", duplicateError.Code);
        reached.Reset();
        release.Reset();
        fault = point =>
        {
            if (point == TransferFaultPoint.HashVerified) { reached.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); }
        };
        var completion = Task.Run(() => service.Complete(device, first.TransferId));
        Assert.True(reached.Wait(TimeSpan.FromSeconds(15)));
        try
        {
            var otherAppend = Task.Run(() => service.Append(device, other.TransferId, 0, payload));
            var progress = await otherAppend.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(payload.Length, progress.TransferredBytes);
        }
        finally { release.Set(); }
        await completion;
    }

    [Fact]
    public void ShutdownStopsNewMutationsAndPreservesDurableStagingForRestart()
    {
        Start();
        var created = Create();
        service!.Append(device, created.TransferId, 0, payload.AsMemory(0, 4));
        service.StopAdmission();
        Assert.Throws<BasicFileTransferException>(() => service.Append(device, created.TransferId, 4, payload.AsMemory(4)));
        Start();
        Assert.Equal(4, service!.GetTransfer(device, created.TransferId).TransferredBytes);
    }

    private sealed class FailingJournal(ITransferJournal inner, bool after, TransferState target) : ITransferJournal
    {
        public IReadOnlyList<DurableTransfer> Load() => inner.Load();
        public void Insert(DurableTransfer record) => inner.Insert(record);
        public void Replace(DurableTransfer previous, DurableTransfer next)
        {
            if (next.State != target) { inner.Replace(previous, next); return; }
            if (after) inner.Replace(previous, next);
            throw new TransferJournalException();
        }
        public void Dispose() => inner.Dispose();
    }

    public void Dispose()
    {
        service?.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class SimulatedCrash : Exception;
    private sealed class Configuration : IShareConfigurationStore
    {
        public ShareConfiguration? Value = new("share", Guid.NewGuid());
        public ShareConfiguration? Read() => Value;
        public ShareConfiguration Save(string rootPath) => Value = new(rootPath, Guid.NewGuid());
        public void Clear() => Value = null;
    }
    private static StagingCapability NewCapability() => new(Guid.NewGuid(), Guid.NewGuid(), new string('1', 48),
        Guid.NewGuid().ToString("N") + new string('0', 16), new string('2', 48), new string('3', 48));
    private sealed class MemoryObject(StagingCapability capability, byte[] bytes)
    {
        internal StagingCapability Capability = capability;
        internal byte[] Bytes = bytes;
    }
    private sealed class MemoryFiles : IShareFileSystem, IDurableShareFileSystem
    {
        internal readonly Dictionary<Guid, MemoryObject> Staging = [];
        internal MemoryObject? Destination;
        internal bool PartialWriteFailure, FlushFailure, TruncateFailure, CleanupFailure;
        internal int Renames, Reopens;
        public IShareFileSession Open(ShareConfiguration configuration) => throw new NotSupportedException();
        public IDurableShareSession OpenDurable(ShareConfiguration configuration) => new Session(this);
        private sealed class Session(MemoryFiles owner) : IDurableShareSession
        {
            public string RootIdentity => new('2', 48);
            public IDurableStagingFile CreateDurable(RelativeSharePath destination)
            {
                var value = new MemoryObject(NewCapability(), []);
                owner.Staging.Add(value.Capability.DirectoryToken, value);
                return new File(owner, value);
            }
            public IDurableStagingFile Reopen(StagingCapability capability, RelativeSharePath destination)
            {
                owner.Reopens++;
                if (!owner.Staging.TryGetValue(capability.DirectoryToken, out var value) || value.Capability != capability) throw new IOException();
                return new File(owner, value);
            }
            public bool VerifyDestination(StagingCapability capability, RelativeSharePath destination, long size, string sha256) =>
                owner.Destination is { } file && file.Capability.FileIdentity == capability.FileIdentity &&
                file.Bytes.LongLength == size && Convert.ToHexStringLower(SHA256.HashData(file.Bytes)) == sha256;
            public void CleanupEmptyDirectory(StagingCapability capability) { }
            public void CleanupOrphans(IReadOnlySet<Guid> referencedDirectories)
            {
                foreach (var key in owner.Staging.Keys.Where(k => !referencedDirectories.Contains(k)).ToArray()) owner.Staging.Remove(key);
            }
            public void Dispose() { }
        }
        private sealed class File(MemoryFiles owner, MemoryObject value) : IDurableStagingFile
        {
            public StagingCapability Capability => value.Capability;
            public long Length => value.Bytes.Length;
            public int Read(long offset, Span<byte> buffer)
            {
                var count = (int)Math.Min(buffer.Length, Length - offset);
                value.Bytes.AsSpan((int)offset, count).CopyTo(buffer);
                return count;
            }
            public void Write(long offset, ReadOnlySpan<byte> buffer)
            {
                var count = owner.PartialWriteFailure ? Math.Max(1, buffer.Length / 2) : buffer.Length;
                Array.Resize(ref value.Bytes, (int)offset + count);
                buffer[..count].CopyTo(value.Bytes.AsSpan((int)offset));
                if (owner.PartialWriteFailure) throw new IOException("disk full after partial write");
            }
            public void Flush() { if (owner.FlushFailure) throw new IOException("flush failed"); }
            public void Truncate(long committedOffset)
            {
                if (owner.TruncateFailure) throw new IOException("truncate failed");
                Array.Resize(ref value.Bytes, (int)committedOffset);
            }
            public void CompleteNoReplace()
            {
                if (owner.Destination is not null) throw new IOException("no replace");
                owner.Destination = value;
                owner.Staging.Remove(value.Capability.DirectoryToken);
                owner.Renames++;
            }
            public void Delete()
            {
                if (owner.CleanupFailure) throw new IOException("cleanup failed");
                owner.Staging.Remove(value.Capability.DirectoryToken);
            }
            public void Dispose() { }
        }
    }
}
