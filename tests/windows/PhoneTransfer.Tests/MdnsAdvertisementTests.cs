using System.Net;
using PhoneTransfer.Infrastructure.Discovery;
using Xunit;

namespace PhoneTransfer.Tests;

public sealed class MdnsAdvertisementTests
{
    [Fact]
    public void DescriptorContainsOnlyStableIdentityAndProtocolVersion()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var advertisement = MdnsAdvertisement.Create(id, IPAddress.Parse("192.168.1.20"), 58443, 7)!;
        Assert.Equal("phone-transfer-11111111222233334444555555555555._phone-transfer._tcp.local", advertisement.InstanceName);
        Assert.Equal(["version", "deviceId"], advertisement.TxtKeys);
        Assert.Equal(["1", id.ToString("D")], advertisement.TxtValues);
        Assert.Equal((ushort)58443, advertisement.Port);
        Assert.Equal((uint)7, advertisement.InterfaceIndex);
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.4", true)]
    [InlineData("172.16.1.1", true)]
    [InlineData("169.254.1.2", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    public void AdvertisementIsLimitedToPrivateIpv4(string address, bool expected)
    {
        var result = MdnsAdvertisement.Create(Guid.NewGuid(), IPAddress.Parse(address), 58443, 1);
        Assert.Equal(expected, result is not null);
    }
}
