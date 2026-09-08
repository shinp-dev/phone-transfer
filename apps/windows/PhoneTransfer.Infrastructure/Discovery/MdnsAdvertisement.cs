using System.Globalization;
using System.Net;

namespace PhoneTransfer.Infrastructure.Discovery;

public sealed class MdnsAdvertisement
{
    public const string ServiceType = "_phone-transfer._tcp";
    public const int ProtocolVersion = 1;

    private MdnsAdvertisement(Guid deviceId, IPAddress address, ushort port, uint interfaceIndex)
    {
        DeviceId = deviceId;
        Address = address;
        Port = port;
        InterfaceIndex = interfaceIndex;
    }

    public Guid DeviceId { get; }
    public IPAddress Address { get; }
    public ushort Port { get; }
    public uint InterfaceIndex { get; }
    public string InstanceName => $"phone-transfer-{DeviceId:N}.{ServiceType}.local";
    public string HostName => $"{Environment.MachineName}.local";
    public string[] TxtKeys => ["version", "deviceId"];
    public string[] TxtValues => [ProtocolVersion.ToString(CultureInfo.InvariantCulture), DeviceId.ToString("D")];

    public static MdnsAdvertisement? Create(Guid deviceId, IPAddress address, int port, uint interfaceIndex)
    {
        if (deviceId == Guid.Empty || !LanAdapters.IsPrivateV4(address) || port is < 1 or > ushort.MaxValue || interfaceIndex == 0)
        {
            return null;
        }
        return new MdnsAdvertisement(deviceId, address, checked((ushort)port), interfaceIndex);
    }
}
