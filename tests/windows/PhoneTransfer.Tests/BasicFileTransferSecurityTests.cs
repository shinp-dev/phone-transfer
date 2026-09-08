using System.Runtime.Versioning;
using System.Security.Cryptography;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Domain;
using PhoneTransfer.Infrastructure.Storage;
using Xunit;

namespace PhoneTransfer.Tests;

[SupportedOSPlatform("windows")]
public sealed class BasicFileTransferSecurityTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PhoneTransferTransferSecurity", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(fixture, "share");

    public BasicFileTransferSecurityTests()
    {
        Directory.CreateDirectory(Root);
    }

    [Fact]
    public void ShareIdsAreOpaqueAndScopedToTheServiceLifetime()
    {
        var device = Device();
        using var first = Service();
        using var second = Service();

        var firstId = Assert.Single(first.ListShares(device)).Id;
        var repeatedId = Assert.Single(first.ListShares(device)).Id;
        var secondId = Assert.Single(second.ListShares(device)).Id;

        Assert.NotEqual(Guid.Empty, firstId);
        Assert.Equal(firstId, repeatedId);
        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public void CancellingARevokedDeviceClosesAndDeletesItsPrivateStaging()
    {
        var device = Device();
        using var service = Service();
        var shareId = Assert.Single(service.ListShares(device)).Id;
        var payload = "untrusted pending bytes"u8.ToArray();
        var transfer = service.CreateTransfer(
            device,
            shareId,
            RelativeSharePath.Parse("pending.bin"),
            payload.Length,
            Convert.ToHexStringLower(SHA256.HashData(payload)),
            Guid.NewGuid());
        service.Append(device, transfer.TransferId, 0, payload);

        Assert.NotEmpty(Directory.GetDirectories(Root, ".phone-transfer-staging-*"));
        Assert.Equal(1, service.CancelDevice(device.DeviceId));
        Assert.Equal(TransferState.Cancelled, service.GetTransfer(device, transfer.TransferId).State);
        Assert.Empty(Directory.GetDirectories(Root, ".phone-transfer-staging-*"));
        Assert.False(File.Exists(Path.Combine(Root, "pending.bin")));
    }

    [Fact]
    public void ClearingTheShareCancelsAnExistingUploadBeforeAnotherWrite()
    {
        var device = Device();
        var store = new MutableConfigurationStore(Root);
        using var service = new BasicFileTransferService(store, new WindowsShareFileSystem());
        var shareId = Assert.Single(service.ListShares(device)).Id;
        var payload = "first chunk"u8.ToArray();
        var transfer = service.CreateTransfer(
            device,
            shareId,
            RelativeSharePath.Parse("pending.bin"),
            payload.Length * 2,
            Convert.ToHexStringLower(SHA256.HashData(payload.Concat(payload).ToArray())),
            Guid.NewGuid());
        service.Append(device, transfer.TransferId, 0, payload);
        store.Clear();

        var error = Assert.Throws<BasicFileTransferException>(() =>
            service.Append(device, transfer.TransferId, payload.Length, payload));
        Assert.Equal("SHARE_NOT_FOUND", error.Code);
        Assert.Equal(TransferState.Cancelled, service.GetTransfer(device, transfer.TransferId).State);
        Assert.Empty(Directory.GetDirectories(Root, ".phone-transfer-staging-*"));
        Assert.False(File.Exists(Path.Combine(Root, "pending.bin")));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }
        catch (IOException)
        {
            // Failed assertions can briefly leave OS-backed handles alive.
        }
    }

    private BasicFileTransferService Service() =>
        new(new FixedConfigurationStore(Root), new WindowsShareFileSystem());

    private static PairedDevice Device()
    {
        var now = DateTimeOffset.UtcNow;
        return new PairedDevice(Guid.NewGuid(), "phone", new string('a', 64), now, now, DevicePermissions.All, false);
    }

    private sealed class FixedConfigurationStore(string root) : IShareConfigurationStore
    {
        public ShareConfiguration? Read() => new(root);
        public ShareConfiguration Save(string rootPath) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
    }

    private sealed class MutableConfigurationStore(string root) : IShareConfigurationStore
    {
        private string? currentRoot = root;
        public ShareConfiguration? Read() => currentRoot is null ? null : new(currentRoot);
        public ShareConfiguration Save(string rootPath)
        {
            currentRoot = rootPath;
            return new(rootPath);
        }
        public void Clear() => currentRoot = null;
    }
}
