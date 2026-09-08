using System.Collections.Concurrent;
using System.Security.Cryptography;
using PhoneTransfer.Domain;

namespace PhoneTransfer.Application.Files;

// Each transfer has one mutation gate. Shared lifecycle readers only coordinate shutdown;
// unrelated transfers never share an exclusive lock during hash/write/flush.
public sealed partial class DurableFileTransferService : IFileTransferService
{
    private sealed class Entry(DurableTransfer record)
    {
        internal readonly object Gate = new();
        internal DurableTransfer Record = record;
    }
    private readonly ConcurrentDictionary<Guid, Entry> transfers = new();
    private readonly ConcurrentDictionary<Guid, object> deviceFences = new();
    private readonly HashSet<Guid> revoked = [];
    private readonly object creationGate = new();
    private readonly ReaderWriterLockSlim lifetime = new();
    private readonly IShareConfigurationStore configurations;
    private readonly IDurableShareFileSystem fileSystem;
    private readonly ITransferJournal journal;
    private readonly BasicFileTransferService browsing;
    private readonly Func<Guid, PairedDevice?> currentDevice;
    private readonly Action<TransferFaultPoint>? fault;
    private readonly TimeProvider clock;
    private bool ready;
    private bool stopped;
    private volatile bool poisoned;

    public DurableFileTransferService(IShareConfigurationStore configurations, IShareFileSystem browsingFileSystem,
        IDurableShareFileSystem fileSystem, ITransferJournal journal, Func<Guid, PairedDevice?> currentDevice,
        Action<TransferFaultPoint>? fault = null, TimeProvider? clock = null)
    {
        this.configurations = configurations;
        this.fileSystem = fileSystem;
        this.journal = journal;
        this.currentDevice = currentDevice;
        this.fault = fault;
        this.clock = clock ?? TimeProvider.System;
        browsing = new BasicFileTransferService(configurations, browsingFileSystem);
    }

    public void Initialize()
    {
        lifetime.EnterWriteLock();
        try
        {
            if (ready || stopped) throw new InvalidOperationException("RECOVERY_LIFETIME_CONFLICT");
            foreach (var record in journal.Load())
                if (!transfers.TryAdd(record.TransferId, new Entry(record))) throw new TransferJournalException();
            if (transfers.Values.Select(e => (e.Record.OwnerDeviceId, e.Record.IdempotencyKey)).Distinct().Count() != transfers.Count ||
                transfers.Values.Select(e => e.Record.Staging.DirectoryToken).Distinct().Count() != transfers.Count)
                throw new TransferJournalException();
            Reconcile();
            ready = true; // The runtime starts Kestrel only after this returns.
        }
        catch { poisoned = true; throw; }
        finally { lifetime.ExitWriteLock(); }
    }

    public IReadOnlyList<ShareDescriptor> ListShares(PairedDevice device) => Operation(() =>
    {
        Authorize(device, DevicePermissions.Browse);
        var configuration = configurations.Read();
        if (configuration is null) return Array.Empty<ShareDescriptor>();
        RequireGeneration(configuration);
        return new[] { new ShareDescriptor(configuration.Generation, "Shared folder", device.Allows(DevicePermissions.Upload)) };
    });

    public IReadOnlyList<FileEntryDescriptor> ListEntries(PairedDevice device, Guid shareId, RelativeSharePath directory) => Operation(() =>
    {
        ResolveShare(device, shareId, DevicePermissions.Browse);
        return browsing.ListEntries(device, browsing.ListShares(device).Single().Id, directory);
    });

    public DownloadLease OpenDownload(PairedDevice device, Guid shareId, RelativeSharePath path) => Operation(() =>
    {
        ResolveShare(device, shareId, DevicePermissions.Download);
        return browsing.OpenDownload(device, browsing.ListShares(device).Single().Id, path);
    });

    public TransferSnapshot CreateTransfer(PairedDevice device, Guid shareId, RelativeSharePath destination,
        long totalSize, string sha256, Guid idempotencyKey) => Operation(() =>
    {
        Authorize(device, DevicePermissions.Upload);
        RelativeSharePath.Parse(destination.Value);
        if (totalSize is < 0 or > BasicFileTransferService.MaximumFileBytes || sha256 is not { Length: 64 } ||
            sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) || idempotencyKey == Guid.Empty)
            throw Error("INVALID_TRANSFER_METADATA");
        // Creation serializes admission/idempotency only. It never waits on an existing transfer's I/O gate.
        lock (creationGate)
        {
            var existing = transfers.Values.FirstOrDefault(e => e.Record.OwnerDeviceId == device.DeviceId && e.Record.IdempotencyKey == idempotencyKey);
            if (existing is not null)
            {
                var record = Volatile.Read(ref existing.Record);
                if (record.ShareId != shareId || record.Destination != destination.Value || record.TotalSize != totalSize || record.Sha256 != sha256)
                    throw Error("IDEMPOTENCY_CONFLICT");
                return Snapshot(record);
            }
            var configuration = ResolveShare(device, shareId, DevicePermissions.Upload);
            var active = transfers.Values.Select(e => Volatile.Read(ref e.Record)).Where(r => !r.Terminal).ToArray();
            if (transfers.Count >= DurableTransferMachine.MaximumRecords) throw Error("TOO_MANY_TRANSFERS");
            if (active.Count(r => r.OwnerDeviceId == device.DeviceId) >= 4) throw Error("TOO_MANY_ACTIVE_TRANSFERS", true);
            if (totalSize > DurableTransferMachine.MaximumStagingBytes - active.Sum(r => r.TotalSize)) throw Error("STAGING_QUOTA_EXCEEDED", true);
            using var session = fileSystem.OpenDurable(configuration);
            using var staging = session.CreateDurable(destination);
            Hit(TransferFaultPoint.StagingCreated);
            var now = clock.GetUtcNow();
            var recordToCreate = new DurableTransfer(Guid.NewGuid(), device.DeviceId, device.CertificateSha256, device.RegisteredAt,
                shareId, destination.Value, totalSize, sha256, idempotencyKey, staging.Capability,
                TransferState.Created, 0, 0, now, now);
            lock (DeviceFence(device.DeviceId))
            {
                Authorize(device, DevicePermissions.Upload);
                EnsureConfiguration(recordToCreate, configuration);
                Journal(() => journal.Insert(recordToCreate));
                transfers[recordToCreate.TransferId] = new Entry(recordToCreate);
            }
            Hit(TransferFaultPoint.TransferCreated);
            return Snapshot(recordToCreate);
        }
    });

    public TransferSnapshot GetTransfer(PairedDevice device, Guid transferId) => Operation(() =>
    {
        Authorize(device, DevicePermissions.Upload);
        return Snapshot(Volatile.Read(ref Owned(device, transferId).Record));
    });

    public TransferSnapshot Append(PairedDevice device, Guid transferId, long requestOffset, ReadOnlyMemory<byte> chunk) => Mutation(device, transferId, entry =>
    {
        var record = entry.Record;
        if (record.State is not (TransferState.Created or TransferState.Transferring or TransferState.Paused)) throw Error("TRANSFER_STATE_CONFLICT");
        long next;
        try { next = TransferOffset.Advance(record.TotalSize, record.CommittedOffset, requestOffset, chunk.Length); }
        catch (ArgumentException) { throw Error("OFFSET_MISMATCH", true); }
        var configuration = ActiveConfiguration(entry);
        using var session = fileSystem.OpenDurable(configuration);
        IDurableStagingFile staging;
        try { staging = session.Reopen(record.Staging, RelativeSharePath.Parse(record.Destination)); }
        catch (IOException) { Fail(entry); throw Error("STAGING_INVALID"); }
        using (staging)
        {
            try { Normalize(staging, record.CommittedOffset); }
            catch (IOException) { Fail(entry); throw Error("STAGING_INVALID"); }
            try
            {
                staging.Write(record.CommittedOffset, chunk.Span);
                Hit(TransferFaultPoint.ChunkWritten);
                staging.Flush();
                Hit(TransferFaultPoint.ChunkFlushed);
            }
            catch (IOException)
            {
                try { staging.Truncate(record.CommittedOffset); }
                catch (IOException) { Fail(entry); throw Error("STORAGE_ROLLBACK_FAILED"); }
                Save(entry, TransferState.Paused);
                throw Error("WRITE_FAILED", true); // Retry only after durable rollback has succeeded.
            }
            lock (DeviceFence(device.DeviceId))
            {
                if (!IsAuthorized(record))
                {
                    Save(entry, TransferState.Cancelled);
                    TryDelete(staging);
                    throw Error("PERMISSION_DENIED");
                }
                EnsureConfiguration(record, configuration);
                // No file I/O or flush occurs inside the journal transaction.
                Save(entry, TransferState.Transferring, next);
            }
            Hit(TransferFaultPoint.OffsetCommitted);
            return Snapshot(entry.Record);
        }
    });

    public TransferSnapshot Complete(PairedDevice device, Guid transferId) => Mutation(device, transferId, entry =>
    {
        if (entry.Record.State == TransferState.Completed) return Snapshot(entry.Record);
        if (entry.Record.Terminal) throw Error("TRANSFER_STATE_CONFLICT");
        if (entry.Record.CommittedOffset != entry.Record.TotalSize) throw Error("TRANSFER_INCOMPLETE", true);
        var configuration = ActiveConfiguration(entry);
        if (entry.Record.State != TransferState.Verifying) Save(entry, TransferState.Verifying);
        Hit(TransferFaultPoint.VerifyingCommitted);
        CompleteStaging(entry, configuration);
        return Snapshot(entry.Record);
    });

    private void CompleteStaging(Entry entry, ShareConfiguration configuration)
    {
        var record = entry.Record;
        using var session = fileSystem.OpenDurable(configuration);
        IDurableStagingFile staging;
        try { staging = session.Reopen(record.Staging, RelativeSharePath.Parse(record.Destination)); }
        catch (IOException) { Fail(entry); throw Error("STAGING_INVALID"); }
        using (staging)
        {
            try
            {
                Normalize(staging, record.CommittedOffset);
                if (!DigestMatches(staging, record.TotalSize, record.Sha256))
                {
                    Fail(entry);
                    TryDelete(staging);
                    throw Error("HASH_MISMATCH");
                }
                // All bytes were durably committed before Verifying. Flush is outside the device fence.
                staging.Flush();
            }
            catch (IOException) { Fail(entry); throw Error("VERIFY_FAILED"); }
            Hit(TransferFaultPoint.HashVerified);
            lock (DeviceFence(record.OwnerDeviceId))
            {
                if (!IsAuthorized(record))
                {
                    Save(entry, TransferState.Cancelled);
                    TryDelete(staging);
                    throw Error("PERMISSION_DENIED");
                }
                EnsureConfiguration(record, configuration);
                try { staging.CompleteNoReplace(); }
                catch (IOException) { Fail(entry); throw Error("DESTINATION_CONFLICT"); }
                Hit(TransferFaultPoint.Renamed);
                // A journal error is ambiguous: poison the service, retain Verifying, NEVER delete destination.
                Save(entry, TransferState.Completed);
            }
            Hit(TransferFaultPoint.CompletedCommitted);
        }
    }

    public TransferSnapshot Cancel(PairedDevice device, Guid transferId) => Mutation(device, transferId, entry =>
    {
        if (entry.Record.State == TransferState.Cancelled) return Snapshot(entry.Record);
        if (entry.Record.Terminal) throw Error("TRANSFER_STATE_CONFLICT");
        Save(entry, TransferState.Cancelled);
        Hit(TransferFaultPoint.CancelCommitted);
        Cleanup(entry.Record);
        return Snapshot(entry.Record);
    });

    // Local host operation. Authorization commit takes precedence and never waits for hash/chunk I/O.
    public bool RevokeDevice(Guid deviceId, Func<bool> revokeAuthorization)
    {
        lifetime.EnterReadLock();
        try
        {
            bool changed;
            lock (DeviceFence(deviceId))
            {
                changed = revokeAuthorization();
                lock (revoked) revoked.Add(deviceId);
            }
            Hit(TransferFaultPoint.RevokeCommitted);
            // Journal failure cannot prevent or roll back a device authorization revoke.
            if (ready && !stopped && !poisoned)
            {
                try { CancelDeviceCore(deviceId); }
                catch (BasicFileTransferException) { /* Startup repeats cancellation from devices.db. */ }
            }
            return changed;
        }
        finally { lifetime.ExitReadLock(); }
    }

    public int CancelDevice(Guid deviceId) => Operation(() =>
    {
        lock (DeviceFence(deviceId)) { lock (revoked) revoked.Add(deviceId); }
        return CancelDeviceCore(deviceId);
    });

    private int CancelDeviceCore(Guid deviceId)
    {
        var count = 0;
        foreach (var entry in transfers.Values.Where(e => e.Record.OwnerDeviceId == deviceId))
        {
            lock (entry.Gate)
            {
                if (entry.Record.Terminal) continue;
                Save(entry, TransferState.Cancelled);
                Cleanup(entry.Record);
                count++;
            }
        }
        return count;
    }

    private T Mutation<T>(PairedDevice device, Guid transferId, Func<Entry, T> action) => Operation(() =>
    {
        Authorize(device, DevicePermissions.Upload);
        var entry = Owned(device, transferId);
        lock (entry.Gate)
        {
            CheckAvailable();
            Authorize(device, DevicePermissions.Upload);
            return action(entry);
        }
    });

    private Entry Owned(PairedDevice device, Guid id) => transfers.TryGetValue(id, out var entry) &&
        entry.Record.OwnerDeviceId == device.DeviceId && entry.Record.OwnerCertificate == device.CertificateSha256 &&
        entry.Record.OwnerRegisteredAt == device.RegisteredAt ? entry : throw Error("TRANSFER_NOT_FOUND");

    private object DeviceFence(Guid id) => deviceFences.GetOrAdd(id, _ => new object());
    private bool IsAuthorized(DurableTransfer record)
    {
        lock (revoked) { if (revoked.Contains(record.OwnerDeviceId)) return false; }
        var current = currentDevice(record.OwnerDeviceId);
        return current is not null && current.Allows(DevicePermissions.Upload) && current.CertificateSha256 == record.OwnerCertificate &&
            current.RegisteredAt == record.OwnerRegisteredAt;
    }
    private void Authorize(PairedDevice supplied, DevicePermissions permission)
    {
        lock (revoked) { if (revoked.Contains(supplied.DeviceId)) throw Error("PERMISSION_DENIED"); }
        var current = currentDevice(supplied.DeviceId);
        if (current is null || !current.Allows(permission) || !supplied.Allows(permission) ||
            current.CertificateSha256 != supplied.CertificateSha256 || current.RegisteredAt != supplied.RegisteredAt)
            throw Error("PERMISSION_DENIED");
    }
    private ShareConfiguration ResolveShare(PairedDevice device, Guid shareId, DevicePermissions permission)
    {
        Authorize(device, permission);
        var configuration = configurations.Read() ?? throw Error("SHARE_NOT_CONFIGURED");
        RequireGeneration(configuration);
        if (shareId != configuration.Generation) throw Error("SHARE_NOT_FOUND");
        return configuration;
    }
    private static void RequireGeneration(ShareConfiguration configuration)
    {
        if (configuration.Generation == Guid.Empty) throw Error("SHARE_UNAVAILABLE");
    }
    private ShareConfiguration ActiveConfiguration(Entry entry)
    {
        var configuration = configurations.Read();
        if (configuration is not null && configuration.Generation == entry.Record.ShareId) return configuration;
        Save(entry, TransferState.Cancelled);
        throw Error("SHARE_NOT_FOUND"); // Never reopen an old root from persisted strings.
    }
    private void EnsureConfiguration(DurableTransfer record, ShareConfiguration expected)
    {
        var current = configurations.Read();
        if (current != expected || current.Generation != record.ShareId) throw Error("SHARE_NOT_FOUND");
    }
    private void Save(Entry entry, TransferState next, long? offset = null)
    {
        var previous = entry.Record;
        var updated = DurableTransferMachine.Move(previous, next, clock.GetUtcNow(), offset);
        Journal(() => journal.Replace(previous, updated));
        Volatile.Write(ref entry.Record, updated);
    }
    private void Journal(Action action)
    {
        if (poisoned) throw Error("TRANSFER_JOURNAL_UNAVAILABLE");
        try { action(); }
        catch (IOException) { poisoned = true; throw Error("TRANSFER_JOURNAL_UNAVAILABLE"); }
    }
    private void Fail(Entry entry) { if (!entry.Record.Terminal) Save(entry, TransferState.Failed); }
    private void Hit(TransferFaultPoint point) => fault?.Invoke(point);
    private static BasicFileTransferException Error(string code, bool retryable = false) =>
        new(code, "The transfer operation could not be completed safely.", retryable);
    private static TransferSnapshot Snapshot(DurableTransfer record) => new(record.TransferId,
        record.Destination[(record.Destination.LastIndexOf('/') + 1)..], record.TotalSize, record.CommittedOffset,
        record.Sha256, record.State, record.UpdatedAt);

    private T Operation<T>(Func<T> action)
    {
        lifetime.EnterReadLock();
        try { CheckAvailable(); return action(); }
        finally { lifetime.ExitReadLock(); }
    }
    private void CheckAvailable()
    {
        if (!ready || stopped || poisoned) throw Error("TRANSFER_SERVICE_UNAVAILABLE");
    }
    public void Dispose()
    {
        lifetime.EnterWriteLock(); // Stops admission and drains readers; forced kill still uses journal recovery.
        try
        {
            if (stopped) return;
            stopped = true;
            browsing.Dispose();
            journal.Dispose();
        }
        finally { lifetime.ExitWriteLock(); }
    }

    private static void Normalize(IDurableStagingFile staging, long offset)
    {
        var length = staging.Length;
        if (length < offset) throw new IOException("COMMITTED_PREFIX_MISSING");
        if (length > offset) staging.Truncate(offset);
    }
    private static bool DigestMatches(IShareReadFile file, long size, string expected)
    {
        if (file.Length != size) return false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long offset = 0;
        while (offset < size)
        {
            var count = file.Read(offset, buffer.AsSpan(0, (int)Math.Min(buffer.Length, size - offset)));
            if (count <= 0) throw new IOException("FILE_ENDED_EARLY");
            hash.AppendData(buffer, 0, count);
            offset += count;
        }
        return CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expected));
    }
    private static void TryDelete(IDurableStagingFile staging)
    {
        try { staging.Delete(); }
        catch (IOException) { /* Terminal authority is already persisted. */ }
    }
}
