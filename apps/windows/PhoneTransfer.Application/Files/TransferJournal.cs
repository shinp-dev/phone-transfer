using PhoneTransfer.Domain;

namespace PhoneTransfer.Application.Files;

public sealed record DurableTransfer(Guid TransferId, Guid OwnerDeviceId, string OwnerCertificate, DateTimeOffset OwnerRegisteredAt,
    Guid ShareId, string Destination, long TotalSize, string Sha256, Guid IdempotencyKey,
    StagingCapability Staging, TransferState State, long CommittedOffset, long Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public bool Terminal => State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled;
}

// Every method finishes its transaction before returning; never calls filesystem/application code.
public interface ITransferJournal : IDisposable
{
    IReadOnlyList<DurableTransfer> Load();
    void Insert(DurableTransfer record);
    void Replace(DurableTransfer previous, DurableTransfer next);
}

public sealed class TransferJournalException : IOException
{
    public TransferJournalException(Exception? inner = null) : base("TRANSFER_JOURNAL_UNAVAILABLE", inner) { }
}

// One auditable state-machine authority. Persistence adapters do not invent transitions.
public static class DurableTransferMachine
{
    public const int MaximumRecords = 4096;
    public const long MaximumStagingBytes = 256L * 1024 * 1024 * 1024;

    public static void Validate(DurableTransfer record)
    {
        if (record.TransferId == Guid.Empty || record.OwnerDeviceId == Guid.Empty || record.ShareId == Guid.Empty ||
            record.IdempotencyKey == Guid.Empty || !Hash(record.OwnerCertificate) || !Hash(record.Sha256) ||
            record.TotalSize is < 0 or > BasicFileTransferService.MaximumFileBytes ||
            record.CommittedOffset < 0 || record.CommittedOffset > record.TotalSize || record.Revision < 0 ||
            !Enum.IsDefined(record.State) || record.Staging is null ||
            record.Staging.DirectoryToken == Guid.Empty || record.Staging.FileToken == Guid.Empty ||
            !Identity(record.Staging.DirectoryIdentity) || !Identity(record.Staging.FileIdentity) ||
            !Identity(record.Staging.RootIdentity) || !Identity(record.Staging.DestinationParentIdentity) ||
            record.State == TransferState.Created && record.CommittedOffset != 0 ||
            record.State is TransferState.Verifying or TransferState.Completed && record.CommittedOffset != record.TotalSize)
            throw new TransferJournalException();
        RelativeSharePath.Parse(record.Destination);
    }

    public static DurableTransfer Move(DurableTransfer record, TransferState state, DateTimeOffset now, long? offset = null)
    {
        var committed = offset ?? record.CommittedOffset;
        if (record.Terminal || committed < record.CommittedOffset || committed > record.TotalSize ||
            committed != record.CommittedOffset && state != TransferState.Transferring)
            throw new InvalidOperationException("INVALID_DURABLE_TRANSITION");
        var allowed = (record.State, state) switch
        {
            (_, TransferState.Failed or TransferState.Cancelled) => true,
            (TransferState.Created or TransferState.Transferring or TransferState.Paused,
                TransferState.Transferring or TransferState.Paused or TransferState.Verifying) => true,
            (TransferState.Verifying, TransferState.Completed) => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("INVALID_DURABLE_TRANSITION");
        var next = record with { State = state, CommittedOffset = committed, Revision = checked(record.Revision + 1), UpdatedAt = now };
        Validate(next);
        return next;
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Identity(string? value) => value is { Length: 48 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

// Optional deterministic seam. Production passes null. Throwing simulates loss of the process
// at a durability boundary; restart tests construct a NEW journal and service instance.
public enum TransferFaultPoint
{
    StagingCreated, TransferCreated, ChunkWritten, ChunkFlushed, OffsetCommitted,
    VerifyingCommitted, HashVerified, Renamed, CompletedCommitted, CancelCommitted,
    RevokeCommitted, ReconciliationStep
}
