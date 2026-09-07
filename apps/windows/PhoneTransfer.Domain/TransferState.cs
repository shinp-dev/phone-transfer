namespace PhoneTransfer.Domain;

public enum TransferState { Created, Transferring, Paused, Verifying, Completed, Failed, Cancelled }

public static class TransferTransitions
{
    public static TransferState Move(TransferState current, TransferState next)
    {
        var allowed = (current, next) switch
        {
            (TransferState.Created, TransferState.Transferring or TransferState.Cancelled or TransferState.Failed) => true,
            (TransferState.Transferring, TransferState.Paused or TransferState.Verifying or TransferState.Cancelled or TransferState.Failed) => true,
            (TransferState.Paused, TransferState.Transferring or TransferState.Cancelled or TransferState.Failed) => true,
            (TransferState.Verifying, TransferState.Completed or TransferState.Failed or TransferState.Cancelled) => true,
            _ => false
        };
        return allowed ? next : throw new InvalidOperationException($"Invalid transfer transition: {current} -> {next}");
    }
}
