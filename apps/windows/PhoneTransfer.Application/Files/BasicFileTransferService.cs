using System.Security.Cryptography;
using PhoneTransfer.Domain;

namespace PhoneTransfer.Application.Files;

public sealed record ShareDescriptor(Guid Id, string Name, bool Writable);

public sealed record FileEntryDescriptor(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long Size,
    DateTimeOffset ModifiedAt);

public sealed record TransferSnapshot(
    Guid TransferId,
    string FileName,
    long TotalSize,
    long TransferredBytes,
    string Sha256,
    TransferState State,
    DateTimeOffset UpdatedAt);

public sealed class BasicFileTransferException : Exception
{
    public BasicFileTransferException(string code, string message, bool retryable = false, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }
    public bool Retryable { get; }
}

public sealed class BasicFileTransferService : IDisposable
{
    public const long MaximumFileBytes = 1_099_511_627_776;
    private const long MaximumActiveStagingBytes = 256L * 1024 * 1024 * 1024;
    private const int MaximumActiveTransfersPerDevice = 4;
    private const int MaximumTransferRecords = 256;
    private const int HashBufferBytes = 1024 * 1024;

    private readonly object gate = new();
    private readonly IShareConfigurationStore configurations;
    private readonly IShareFileSystem fileSystem;
    private readonly TimeProvider clock;
    private readonly Dictionary<Guid, TransferRecord> transfers = [];
    private readonly Dictionary<(Guid DeviceId, Guid IdempotencyKey), Guid> idempotency = [];
    private string? currentShareRoot;
    private Guid currentShareId;
    private bool disposed;

    public BasicFileTransferService(
        IShareConfigurationStore configurations,
        IShareFileSystem fileSystem,
        TimeProvider? clock = null)
    {
        this.configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        this.clock = clock ?? TimeProvider.System;
    }

    public IReadOnlyList<ShareDescriptor> ListShares(PairedDevice device)
    {
        Require(device, DevicePermissions.Browse);
        var configuration = ReadConfiguration();
        if (configuration is null) return [];
        return [new ShareDescriptor(GetShareId(configuration), "Shared folder", device.Allows(DevicePermissions.Upload))];
    }

    public IReadOnlyList<FileEntryDescriptor> ListEntries(PairedDevice device, Guid shareId, RelativeSharePath directory)
    {
        var configuration = ResolveShare(device, shareId, DevicePermissions.Browse);
        try
        {
            using var session = fileSystem.Open(configuration);
            return session.List(directory, 200).Select(entry => new FileEntryDescriptor(
                entry.Name,
                directory.Value.Length == 0 ? entry.Name : $"{directory.Value}/{entry.Name}",
                entry.IsDirectory,
                entry.Size,
                entry.ModifiedAt)).ToArray();
        }
        catch (IOException exception)
        {
            throw new BasicFileTransferException("PATH_UNAVAILABLE", "The requested path is unavailable.", true, exception);
        }
    }

    public TransferSnapshot CreateTransfer(
        PairedDevice device,
        Guid shareId,
        RelativeSharePath destination,
        long totalSize,
        string sha256,
        Guid idempotencyKey)
    {
        lock (gate)
        {
            CheckOpen();
            var configuration = ResolveShare(device, shareId, DevicePermissions.Upload);
            ValidateTransferMetadata(destination, totalSize, sha256, idempotencyKey);
            var key = (device.DeviceId, idempotencyKey);
            if (idempotency.TryGetValue(key, out var existingId) && transfers.TryGetValue(existingId, out var existing))
            {
                if (existing.ShareId != shareId || existing.Destination.Value != destination.Value ||
                    existing.TotalSize != totalSize || !string.Equals(existing.Sha256, sha256, StringComparison.Ordinal))
                    throw new BasicFileTransferException(
                        "IDEMPOTENCY_CONFLICT", "The idempotency key is already bound to different transfer metadata.");
                return Snapshot(existing);
            }

            EnsureRecordCapacity();
            var activeForDevice = 0;
            long activeBytes = 0;
            foreach (var transfer in transfers.Values)
            {
                if (IsTerminal(transfer.State)) continue;
                if (transfer.OwnerDeviceId == device.DeviceId) activeForDevice++;
                if (transfer.TotalSize > MaximumActiveStagingBytes - activeBytes)
                    activeBytes = MaximumActiveStagingBytes;
                else activeBytes += transfer.TotalSize;
            }
            if (activeForDevice >= MaximumActiveTransfersPerDevice)
                throw new BasicFileTransferException("TOO_MANY_ACTIVE_TRANSFERS", "Too many transfers are active for this device.", true);
            if (totalSize > MaximumActiveStagingBytes - activeBytes)
                throw new BasicFileTransferException("STAGING_QUOTA_EXCEEDED", "The staging quota is exhausted.", true);

            IShareFileSession? session = null;
            try
            {
                session = fileSystem.Open(configuration);
                var staging = session.CreateStaging(destination);
                var now = clock.GetUtcNow();
                var record = new TransferRecord(
                    Guid.NewGuid(),
                    device.DeviceId,
                    shareId,
                    destination,
                    totalSize,
                    sha256,
                    idempotencyKey,
                    TransferState.Created,
                    0,
                    now,
                    session,
                    staging);
                transfers.Add(record.TransferId, record);
                idempotency.Add(key, record.TransferId);
                session = null;
                return Snapshot(record);
            }
            catch (IOException exception)
            {
                throw new BasicFileTransferException("PATH_UNAVAILABLE", "The upload destination is unavailable.", true, exception);
            }
            finally
            {
                session?.Dispose();
            }
        }
    }

    public TransferSnapshot GetTransfer(PairedDevice device, Guid transferId)
    {
        lock (gate)
        {
            CheckOpen();
            Require(device, DevicePermissions.Upload);
            return Snapshot(OwnedTransfer(device, transferId));
        }
    }

    public TransferSnapshot Append(PairedDevice device, Guid transferId, long requestOffset, ReadOnlyMemory<byte> chunk)
    {
        lock (gate)
        {
            CheckOpen();
            Require(device, DevicePermissions.Upload);
            var transfer = OwnedTransfer(device, transferId);
            if (transfer.State is not (TransferState.Created or TransferState.Transferring or TransferState.Paused))
                throw new BasicFileTransferException("TRANSFER_STATE_CONFLICT", "The transfer cannot accept content in its current state.");
            EnsureTransferShareCurrent(transfer);

            long next;
            try
            {
                next = TransferOffset.Advance(transfer.TotalSize, transfer.CommittedBytes, requestOffset, chunk.Length);
            }
            catch (ArgumentException exception)
            {
                throw new BasicFileTransferException("OFFSET_MISMATCH", "The chunk does not match the committed upload offset.", true, exception);
            }

            try
            {
                transfer.Staging!.Write(transfer.CommittedBytes, chunk.Span);
                transfer.Staging.Flush();
                if (transfer.State != TransferState.Transferring)
                    transfer.State = TransferTransitions.Move(transfer.State, TransferState.Transferring);
                transfer.CommittedBytes = next;
                transfer.UpdatedAt = clock.GetUtcNow();
                return Snapshot(transfer);
            }
            catch (IOException exception)
            {
                FailAndCleanup(transfer);
                throw new BasicFileTransferException("WRITE_FAILED", "The chunk could not be committed.", true, exception);
            }
        }
    }

    public TransferSnapshot Complete(PairedDevice device, Guid transferId)
    {
        lock (gate)
        {
            CheckOpen();
            Require(device, DevicePermissions.Upload);
            var transfer = OwnedTransfer(device, transferId);
            if (transfer.State == TransferState.Completed) return Snapshot(transfer);
            if (IsTerminal(transfer.State))
                throw new BasicFileTransferException("TRANSFER_STATE_CONFLICT", "The transfer is already terminal.");
            EnsureTransferShareCurrent(transfer);
            if (transfer.CommittedBytes != transfer.TotalSize)
                throw new BasicFileTransferException("TRANSFER_INCOMPLETE", "The transfer has not received all bytes yet.", true);

            if (transfer.State == TransferState.Created)
            {
                if (transfer.TotalSize != 0)
                    throw new BasicFileTransferException("TRANSFER_STATE_CONFLICT", "The transfer has not started.");
                transfer.State = TransferTransitions.Move(transfer.State, TransferState.Transferring);
            }
            if (transfer.State != TransferState.Transferring)
                throw new BasicFileTransferException("TRANSFER_STATE_CONFLICT", "The transfer cannot be completed in its current state.");

            transfer.State = TransferTransitions.Move(transfer.State, TransferState.Verifying);
            transfer.UpdatedAt = clock.GetUtcNow();
            try
            {
                if (transfer.Staging!.Length != transfer.TotalSize || !DigestMatches(transfer.Staging, transfer.Sha256))
                {
                    FailAndCleanup(transfer);
                    throw new BasicFileTransferException("HASH_MISMATCH", "The uploaded bytes do not match the expected SHA-256.");
                }

                if (transfer.Staging.DestinationExists())
                {
                    FailAndCleanup(transfer);
                    throw new BasicFileTransferException("DESTINATION_EXISTS", "The destination already exists.");
                }

                try
                {
                    transfer.Staging.CompleteNoReplace();
                }
                catch (IOException exception)
                {
                    FailAndCleanup(transfer);
                    throw new BasicFileTransferException(
                        "DESTINATION_CONFLICT", "The destination could not be completed without replacement.", true, exception);
                }

                // The rename is the commit point. Cleanup failure after this point must never rewrite a committed file as failed.
                transfer.Staging = null;
                transfer.State = TransferTransitions.Move(transfer.State, TransferState.Completed);
                transfer.UpdatedAt = clock.GetUtcNow();
                var completed = Snapshot(transfer);
                var session = transfer.Session;
                transfer.Session = null;
                try
                {
                    session?.Dispose();
                }
                catch (IOException)
                {
                    // A private empty staging directory may remain. It is hidden and never adopted by future sessions.
                    // Durable startup cleanup belongs to the recovery increment.
                }
                return completed;
            }
            catch (BasicFileTransferException)
            {
                throw;
            }
            catch (IOException exception)
            {
                FailAndCleanup(transfer);
                throw new BasicFileTransferException("VERIFY_FAILED", "The staged file could not be verified.", true, exception);
            }
        }
    }

    public TransferSnapshot Cancel(PairedDevice device, Guid transferId)
    {
        lock (gate)
        {
            CheckOpen();
            Require(device, DevicePermissions.Upload);
            var transfer = OwnedTransfer(device, transferId);
            if (transfer.State == TransferState.Cancelled) return Snapshot(transfer);
            if (IsTerminal(transfer.State))
                throw new BasicFileTransferException("TRANSFER_STATE_CONFLICT", "The transfer is already terminal.");
            transfer.State = TransferTransitions.Move(transfer.State, TransferState.Cancelled);
            transfer.UpdatedAt = clock.GetUtcNow();
            CleanupResources(transfer);
            return Snapshot(transfer);
        }
    }

    public int CancelDevice(Guid deviceId)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("Device ID must not be empty.", nameof(deviceId));
        lock (gate)
        {
            CheckOpen();
            var cancelled = 0;
            Exception? cleanupError = null;
            foreach (var transfer in transfers.Values)
            {
                if (transfer.OwnerDeviceId != deviceId || IsTerminal(transfer.State)) continue;
                transfer.State = TransferState.Cancelled;
                transfer.UpdatedAt = clock.GetUtcNow();
                try
                {
                    CleanupResources(transfer);
                }
                catch (IOException exception)
                {
                    cleanupError ??= exception;
                }
                cancelled++;
            }
            if (cleanupError is not null)
                throw new IOException("REVOKED_DEVICE_STAGING_CLEANUP_FAILED", cleanupError);
            return cancelled;
        }
    }

    public DownloadLease OpenDownload(PairedDevice device, Guid shareId, RelativeSharePath path)
    {
        var configuration = ResolveShare(device, shareId, DevicePermissions.Download);
        IShareFileSession? session = null;
        IShareReadFile? file = null;
        try
        {
            session = fileSystem.Open(configuration);
            file = session.OpenRead(path);
            var lease = new DownloadLease(session, file);
            session = null;
            file = null;
            return lease;
        }
        catch (IOException exception)
        {
            throw new BasicFileTransferException("PATH_UNAVAILABLE", "The requested file is unavailable.", true, exception);
        }
        finally
        {
            file?.Dispose();
            session?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var transfer in transfers.Values) CleanupResources(transfer);
        }
    }

    private ShareConfiguration? ReadConfiguration()
    {
        try
        {
            return configurations.Read();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            throw new BasicFileTransferException("SHARE_UNAVAILABLE", "The configured share is unavailable.", true, exception);
        }
    }

    private ShareConfiguration ResolveShare(PairedDevice device, Guid shareId, DevicePermissions required)
    {
        Require(device, required);
        var configuration = ReadConfiguration()
            ?? throw new BasicFileTransferException("SHARE_NOT_CONFIGURED", "No shared folder is configured.");
        if (shareId == Guid.Empty || GetShareId(configuration) != shareId)
            throw new BasicFileTransferException("SHARE_NOT_FOUND", "The requested share does not exist.");
        return configuration;
    }

    private static void Require(PairedDevice device, DevicePermissions permission)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.Allows(permission))
            throw new BasicFileTransferException("PERMISSION_DENIED", "The paired device does not have the required permission.");
    }

    private static void ValidateTransferMetadata(
        RelativeSharePath destination,
        long totalSize,
        string sha256,
        Guid idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(destination);
        RelativeSharePath.Parse(destination.Value);
        if (totalSize is < 0 or > MaximumFileBytes)
            throw new BasicFileTransferException("INVALID_TRANSFER_SIZE", "The requested transfer size is invalid.");
        if (sha256.Length != 64 || sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new BasicFileTransferException("INVALID_SHA256", "The SHA-256 value must be lowercase hexadecimal.");
        if (idempotencyKey == Guid.Empty)
            throw new BasicFileTransferException("INVALID_IDEMPOTENCY_KEY", "The idempotency key must not be empty.");
    }

    private Guid GetShareId(ShareConfiguration configuration)
    {
        lock (gate)
        {
            CheckOpen();
            if (currentShareId == Guid.Empty || !string.Equals(currentShareRoot, configuration.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                currentShareRoot = configuration.RootPath;
                currentShareId = Guid.NewGuid();
            }
            return currentShareId;
        }
    }

    private void EnsureTransferShareCurrent(TransferRecord transfer)
    {
        var configuration = ReadConfiguration();
        if (configuration is not null && GetShareId(configuration) == transfer.ShareId) return;
        transfer.State = TransferTransitions.Move(transfer.State, TransferState.Cancelled);
        transfer.UpdatedAt = clock.GetUtcNow();
        CleanupResources(transfer);
        throw new BasicFileTransferException("SHARE_NOT_FOUND", "The configured share changed during this transfer.");
    }

    private TransferRecord OwnedTransfer(PairedDevice device, Guid transferId)
    {
        if (transferId == Guid.Empty || !transfers.TryGetValue(transferId, out var transfer) || transfer.OwnerDeviceId != device.DeviceId)
            throw new BasicFileTransferException("TRANSFER_NOT_FOUND", "The requested transfer does not exist.");
        return transfer;
    }

    private void EnsureRecordCapacity()
    {
        if (transfers.Count < MaximumTransferRecords) return;
        var removable = transfers.Values.Where(transfer => IsTerminal(transfer.State)).OrderBy(transfer => transfer.UpdatedAt).FirstOrDefault();
        if (removable is null)
            throw new BasicFileTransferException("TOO_MANY_TRANSFERS", "The transfer registry is full.", true);
        transfers.Remove(removable.TransferId);
        idempotency.Remove((removable.OwnerDeviceId, removable.IdempotencyKey));
    }

    private static bool DigestMatches(IShareReadFile file, string expected)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HashBufferBytes];
        long offset = 0;
        while (offset < file.Length)
        {
            var count = file.Read(offset, buffer.AsSpan(0, (int)Math.Min(buffer.Length, file.Length - offset)));
            if (count <= 0) throw new IOException("STAGED_FILE_ENDED_EARLY");
            hash.AppendData(buffer, 0, count);
            offset += count;
        }
        return CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expected));
    }

    private void FailAndCleanup(TransferRecord transfer)
    {
        if (!IsTerminal(transfer.State)) transfer.State = TransferState.Failed;
        transfer.UpdatedAt = clock.GetUtcNow();
        CleanupResources(transfer);
    }

    private static void CleanupResources(TransferRecord transfer)
    {
        try
        {
            transfer.Staging?.Dispose();
        }
        finally
        {
            transfer.Staging = null;
            transfer.Session?.Dispose();
            transfer.Session = null;
        }
    }

    private static TransferSnapshot Snapshot(TransferRecord transfer) => new(
        transfer.TransferId,
        transfer.Destination.Value[(transfer.Destination.Value.LastIndexOf('/') + 1)..],
        transfer.TotalSize,
        transfer.CommittedBytes,
        transfer.Sha256,
        transfer.State,
        transfer.UpdatedAt);

    private static bool IsTerminal(TransferState state) =>
        state is TransferState.Completed or TransferState.Failed or TransferState.Cancelled;

    private void CheckOpen() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class TransferRecord(
        Guid transferId,
        Guid ownerDeviceId,
        Guid shareId,
        RelativeSharePath destination,
        long totalSize,
        string sha256,
        Guid idempotencyKey,
        TransferState state,
        long committedBytes,
        DateTimeOffset updatedAt,
        IShareFileSession session,
        IShareStagingFile staging)
    {
        internal Guid TransferId { get; } = transferId;
        internal Guid OwnerDeviceId { get; } = ownerDeviceId;
        internal Guid ShareId { get; } = shareId;
        internal RelativeSharePath Destination { get; } = destination;
        internal long TotalSize { get; } = totalSize;
        internal string Sha256 { get; } = sha256;
        internal Guid IdempotencyKey { get; } = idempotencyKey;
        internal TransferState State { get; set; } = state;
        internal long CommittedBytes { get; set; } = committedBytes;
        internal DateTimeOffset UpdatedAt { get; set; } = updatedAt;
        internal IShareFileSession? Session { get; set; } = session;
        internal IShareStagingFile? Staging { get; set; } = staging;
    }
}

public sealed class DownloadLease : IDisposable
{
    private const int HashBufferBytes = 1024 * 1024;
    private readonly IShareFileSession session;
    private readonly IShareReadFile file;
    private bool disposed;

    internal DownloadLease(IShareFileSession session, IShareReadFile file)
    {
        this.session = session;
        this.file = file;
        Length = file.Length;
        ETag = $"\"{Hash(file)}\"";
    }

    public long Length { get; }
    public string ETag { get; }

    public int Read(long offset, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (offset < 0 || offset > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return file.Read(offset, buffer);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            file.Dispose();
        }
        finally
        {
            session.Dispose();
        }
    }

    private static string Hash(IShareReadFile file)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HashBufferBytes];
        long offset = 0;
        while (offset < file.Length)
        {
            var count = file.Read(offset, buffer.AsSpan(0, (int)Math.Min(buffer.Length, file.Length - offset)));
            if (count <= 0) throw new IOException("DOWNLOAD_FILE_ENDED_EARLY");
            hash.AppendData(buffer, 0, count);
            offset += count;
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
