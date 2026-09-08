using System.Security.Cryptography;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Domain;
using Xunit;

namespace PhoneTransfer.Tests;

public sealed class BasicFileTransferCommitTests
{
    [Fact]
    public void SuccessfulRenameRemainsCompletedWhenSessionCleanupFails()
    {
        var store = new FixedConfigurationStore(@"C:\share");
        var fileSystem = new CleanupFailingFileSystem();
        var now = DateTimeOffset.UtcNow;
        var device = new PairedDevice(Guid.NewGuid(), "phone", new string('a', 64), now, now, DevicePermissions.All, false);
        using var service = new BasicFileTransferService(store, fileSystem);
        var shareId = Assert.Single(service.ListShares(device)).Id;
        var payload = "committed"u8.ToArray();
        var transfer = service.CreateTransfer(
            device,
            shareId,
            RelativeSharePath.Parse("final.bin"),
            payload.Length,
            Convert.ToHexStringLower(SHA256.HashData(payload)),
            Guid.NewGuid());
        service.Append(device, transfer.TransferId, 0, payload);

        var completed = service.Complete(device, transfer.TransferId);

        Assert.Equal(TransferState.Completed, completed.State);
        Assert.Equal(TransferState.Completed, service.GetTransfer(device, transfer.TransferId).State);
        Assert.True(fileSystem.Staging.Completed);
    }

    private sealed class FixedConfigurationStore(string root) : IShareConfigurationStore
    {
        public ShareConfiguration? Read() => new(root);
        public ShareConfiguration Save(string rootPath) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
    }

    private sealed class CleanupFailingFileSystem : IShareFileSystem
    {
        public MemoryStagingFile Staging { get; } = new();
        public IShareFileSession Open(ShareConfiguration configuration) => new Session(Staging);

        private sealed class Session(MemoryStagingFile staging) : IShareFileSession
        {
            public IReadOnlyList<ShareEntry> List(RelativeSharePath directory, int limit = 200) => [];
            public IShareReadFile OpenRead(RelativeSharePath file) => throw new NotSupportedException();
            public IShareStagingFile CreateStaging(RelativeSharePath destination) => staging;
            public void Dispose() => throw new IOException("simulated post-commit cleanup failure");
        }
    }

    private sealed class MemoryStagingFile : IShareStagingFile
    {
        private byte[] bytes = [];
        public bool Completed { get; private set; }
        public long Length => bytes.LongLength;

        public int Read(long offset, Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, bytes.LongLength - offset);
            if (count <= 0) return 0;
            bytes.AsSpan((int)offset, count).CopyTo(buffer);
            return count;
        }

        public void Write(long offset, ReadOnlySpan<byte> buffer)
        {
            var required = checked((int)(offset + buffer.Length));
            if (bytes.Length < required) Array.Resize(ref bytes, required);
            buffer.CopyTo(bytes.AsSpan((int)offset));
        }

        public void Flush() { }
        public bool DestinationExists() => false;
        public void CompleteNoReplace() => Completed = true;
        public void Dispose() { }
    }
}
