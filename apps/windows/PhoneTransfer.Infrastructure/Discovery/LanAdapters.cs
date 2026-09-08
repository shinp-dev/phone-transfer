using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PhoneTransfer.Infrastructure.Discovery;

public sealed record LanAdapter(string Name, IPAddress Address, uint InterfaceIndex);

public static class LanAdapters
{
    public static IReadOnlyList<LanAdapter> Find()
    {
        var result = new List<LanAdapter>();
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
            .OrderByDescending(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);

        foreach (var adapter in adapters)
        {
            try
            {
                var properties = adapter.GetIPProperties();
                var ipv4 = properties.GetIPv4Properties();
                if (ipv4 is null) continue;
                foreach (var address in properties.UnicastAddresses)
                {
                    if (IsPrivateV4(address.Address))
                    {
                        result.Add(new LanAdapter(adapter.Name, address.Address, checked((uint)ipv4.Index)));
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // Some virtual/runner adapters report Up while IPv4 is not configured. Ignore them.
            }
        }
        return result;
    }

    public static uint? FindInterfaceIndex(IPAddress address)
    {
        if (!IsPrivateV4(address)) return null;
        return Find().FirstOrDefault(adapter => adapter.Address.Equals(address))?.InterfaceIndex;
    }

    public static bool IsPrivateV4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
    }
}
