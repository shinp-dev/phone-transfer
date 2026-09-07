using System.Security.Cryptography;
using PhoneTransfer.Application;
using PhoneTransfer.Domain;
using Xunit;

namespace PhoneTransfer.Tests;

public class DomainTests
{
    [Theory]
    [InlineData("../Windows/System32")]
    [InlineData("photos/../../private")]
    [InlineData("%2e%2e/secret")]
    [InlineData("%252e%252e/secret")]
    [InlineData("/Windows")]
    [InlineData("C:/Windows")]
    [InlineData("\\\\server\\share")]
    [InlineData("file.txt:secret")]
    [InlineData("photos//file")]
    [InlineData("photos/.")]
    [InlineData("photos/a.")]
    [InlineData("photos/a ")]
    [InlineData("NUL.txt")]
    [InlineData("COM¹.txt")]
    [InlineData("LPT9")]
    [InlineData("a\0b")]
    [InlineData("e\u0301.txt")]
    public void RejectsAmbiguousPaths(string value) =>
        Assert.Throws<ArgumentException>(() => RelativeSharePath.Parse(value));

    [Theory]
    [InlineData("写真/旅行.jpg")]
    [InlineData("資料/a b.pdf")]
    [InlineData("é.txt")]
    public void AcceptsNormalRelativePaths(string value) => Assert.Equal(value, RelativeSharePath.Parse(value).Value);

    [Fact]
    public void CompletedTransferCannotBeCancelledOrRestarted()
    {
        Assert.Throws<InvalidOperationException>(() => TransferTransitions.Move(TransferState.Completed, TransferState.Cancelled));
        Assert.Throws<InvalidOperationException>(() => TransferTransitions.Move(TransferState.Cancelled, TransferState.Completed));
        Assert.Throws<InvalidOperationException>(() => TransferTransitions.Move(TransferState.Created, TransferState.Completed));
    }

    [Fact]
    public void PausedTransferCanResume() => Assert.Equal(TransferState.Transferring,
        TransferTransitions.Move(TransferState.Paused, TransferState.Transferring));

    [Fact]
    public void OffsetSupportsLargeFiles() => Assert.Equal(3_000_000_100L,
        TransferOffset.Advance(4_000_000_000L, 3_000_000_000L, 3_000_000_000L, 100));

    [Theory]
    [InlineData(100, 20, 10, 10)]
    [InlineData(100, 90, 90, 11)]
    [InlineData(100, 0, 0, 0)]
    [InlineData(long.MaxValue, long.MaxValue - 1, long.MaxValue - 1, 2)]
    public void RejectsInvalidOffset(long total, long committed, long offset, int count) =>
        Assert.Throws<ArgumentException>(() => TransferOffset.Advance(total, committed, offset, count));

    [Fact]
    public void RevocationOverridesPermissions()
    {
        var device = new PairedDevice(Guid.NewGuid(), "Phone", "hash", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DevicePermissions.All, true);
        Assert.False(device.Allows(DevicePermissions.Browse));
        Assert.True((device with { Revoked = false }).Allows(DevicePermissions.Upload));
        Assert.False((device with { Revoked = false, Permissions = DevicePermissions.Browse }).Allows(DevicePermissions.Upload));
    }

    [Fact]
    public async Task DigestDetectsChangedContent()
    {
        var expected = Convert.ToHexString(SHA256.HashData("hello"u8));
        Assert.True(await TransferDigest.VerifyAsync(new MemoryStream("hello"u8.ToArray()), expected, CancellationToken.None));
        Assert.False(await TransferDigest.VerifyAsync(new MemoryStream("world"u8.ToArray()), expected, CancellationToken.None));
    }

    [Fact]
    public async Task DigestPropagatesCancellation()
    {
        using var source = new MemoryStream(new byte[1024]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransferDigest.VerifyAsync(source, new string('0', 64), new CancellationToken(true)));
    }
}
