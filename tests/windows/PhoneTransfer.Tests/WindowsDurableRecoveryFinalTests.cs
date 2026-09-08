using System.Security.Cryptography;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Domain;
using PhoneTransfer.Infrastructure.Persistence;
using PhoneTransfer.Infrastructure.Storage;
using Xunit;

namespace PhoneTransfer.Tests;

public sealed partial class WindowsShareFileSystemTests
{
    [Fact]
    public void NativePartialTailLeftByCrashIsTruncatedToCommittedOffsetAcrossRestarts()
    {
        var dataDirectory = Path.Combine(fixture, "partial-tail-data");
        var configuration = new WindowsShareConfigurationStore(dataDirectory);
        configuration.Save(Root);
        var now = DateTimeOffset.UtcNow;
        var device = new PairedDevice(Guid.NewGuid(), "phone", new string('a', 64), now, now, DevicePermissions.All, false);
        var database = Path.Combine(dataDirectory, "transfers.db");
        var fileSystem = new WindowsShareFileSystem();
        var bytes = "durable-partial-tail"u8.ToArray();
        Guid transferId;
        StagingCapability capability;

        {
            var transferJournal = new SqliteTransferJournal(database);
            using var transferService = new DurableFileTransferService(configuration, fileSystem, fileSystem,
                transferJournal, _ => device);
            transferService.Initialize();
            var share = Assert.Single(transferService.ListShares(device));
            transferId = transferService.CreateTransfer(device, share.Id, Relative("partial.bin"), bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), Guid.NewGuid()).TransferId;
            transferService.Append(device, transferId, 0, bytes.AsMemory(0, 4));
            var record = Assert.Single(transferJournal.Load());
            Assert.Equal(4, record.CommittedOffset);
            capability = record.Staging;
        }

        // Test-only operator path: model a process dying after a short write has extended staging but
        // before any flush/DB offset commit. Make the tail durable to exercise the stronger recovery case.
        using (var stream = new FileStream(PrivateFile(capability), FileMode.Open, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            stream.Position = 4;
            stream.Write(bytes.AsSpan(4, 3));
            stream.Flush(true);
        }
        Assert.Equal(7, new FileInfo(PrivateFile(capability)).Length);

        for (var restart = 0; restart < 2; restart++)
        {
            using var transferService = new DurableFileTransferService(configuration, fileSystem, fileSystem,
                new SqliteTransferJournal(database), _ => device);
            transferService.Initialize();
            var recovered = transferService.GetTransfer(device, transferId);
            Assert.Equal(4, recovered.TransferredBytes);
            Assert.Equal(TransferState.Paused, recovered.State);
            Assert.Equal(4, new FileInfo(PrivateFile(capability)).Length);
        }
    }

    [Fact]
    public void NativeDestinationWithSameBytesAndSizeButDifferentFileIdNeverBecomesCompleted()
    {
        var dataDirectory = Path.Combine(fixture, "destination-impostor-data");
        var configuration = new WindowsShareConfigurationStore(dataDirectory);
        configuration.Save(Root);
        var now = DateTimeOffset.UtcNow;
        var device = new PairedDevice(Guid.NewGuid(), "phone", new string('a', 64), now, now, DevicePermissions.All, false);
        var database = Path.Combine(dataDirectory, "transfers.db");
        var fileSystem = new WindowsShareFileSystem();
        var bytes = "same bytes are not identity"u8.ToArray();
        var destination = Path.Combine(Root, "identity.bin");
        Guid transferId;

        using (var transferService = new DurableFileTransferService(configuration, fileSystem, fileSystem,
                   new SqliteTransferJournal(database), _ => device,
                   point => { if (point == TransferFaultPoint.Renamed) throw new NativeCrash(); }))
        {
            transferService.Initialize();
            var share = Assert.Single(transferService.ListShares(device));
            transferId = transferService.CreateTransfer(device, share.Id, Relative("identity.bin"), bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), Guid.NewGuid()).TransferId;
            transferService.Append(device, transferId, 0, bytes);
            Assert.Throws<NativeCrash>(() => transferService.Complete(device, transferId));
        }

        // Preserve the actually committed object elsewhere so NTFS cannot satisfy the check by reusing
        // the same object. A new destination has identical content/size but a different FILE_ID_INFO id.
        File.Move(destination, Path.Combine(Outside, "committed-original.bin"));
        File.WriteAllBytes(destination, bytes);

        for (var restart = 0; restart < 2; restart++)
        {
            using var transferService = new DurableFileTransferService(configuration, fileSystem, fileSystem,
                new SqliteTransferJournal(database), _ => device);
            transferService.Initialize();
            Assert.Equal(TransferState.Failed, transferService.GetTransfer(device, transferId).State);
            Assert.Equal(bytes, File.ReadAllBytes(destination));
        }
    }

    [Fact]
    public void DurableOrphanScanStopsAtTheDocumentedDirectRootBound()
    {
        for (var index = 0; index < 4097; index++)
            File.WriteAllText(Path.Combine(Root, $"ordinary-{index:D4}.txt"), string.Empty);

        using var session = DurableOpen();
        var error = Assert.Throws<IOException>(() => session.CleanupOrphans(new HashSet<Guid>()));
        Assert.Equal("RECOVERY_SCAN_LIMIT", error.Message);
    }
}
