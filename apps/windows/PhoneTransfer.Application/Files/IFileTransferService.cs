using PhoneTransfer.Domain;

namespace PhoneTransfer.Application.Files;

public interface IFileTransferService : IDisposable
{
    IReadOnlyList<ShareDescriptor> ListShares(PairedDevice device);
    IReadOnlyList<FileEntryDescriptor> ListEntries(PairedDevice device, Guid shareId, RelativeSharePath directory);
    TransferSnapshot CreateTransfer(PairedDevice device, Guid shareId, RelativeSharePath destination,
        long totalSize, string sha256, Guid idempotencyKey);
    TransferSnapshot GetTransfer(PairedDevice device, Guid transferId);
    TransferSnapshot Append(PairedDevice device, Guid transferId, long requestOffset, ReadOnlyMemory<byte> chunk);
    TransferSnapshot Complete(PairedDevice device, Guid transferId);
    TransferSnapshot Cancel(PairedDevice device, Guid transferId);
    int CancelDevice(Guid deviceId);
    DownloadLease OpenDownload(PairedDevice device, Guid shareId, RelativeSharePath path);
}
