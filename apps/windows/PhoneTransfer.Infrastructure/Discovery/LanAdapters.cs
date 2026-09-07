using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PhoneTransfer.Infrastructure.Discovery;

public sealed record LanAdapter(string Name, IPAddress Address);

public static class LanAdapters
{
    public static IReadOnlyList<LanAdapter> Find() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
            adapter.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
        .OrderByDescending(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
            .Where(address => IsPrivateV4(address.Address))
            .Select(address => new LanAdapter(adapter.Name, address.Address)))
        .ToArray();

    private static bool IsPrivateV4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
    }
}
