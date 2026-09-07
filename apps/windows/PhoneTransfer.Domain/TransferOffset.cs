namespace PhoneTransfer.Domain;

public static class TransferOffset
{
    public const int MaximumChunkBytes = 4 * 1024 * 1024;
    public static long Advance(long total, long committed, long requestOffset, int chunkSize)
    {
        if (total < 0 || committed < 0 || committed > total || requestOffset != committed
            || chunkSize <= 0 || chunkSize > MaximumChunkBytes || chunkSize > total - committed)
            throw new ArgumentException("Chunk does not match the durable upload offset.");
        return checked(committed + chunkSize);
    }
}
