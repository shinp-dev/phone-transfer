using System.Net;
using PhoneTransfer.Infrastructure.Discovery;

namespace PhoneTransfer.Host;

public sealed record WindowsLanAdapter(string Name, IPAddress Address);

public static class WindowsLanAdapters
{
    public static IReadOnlyList<WindowsLanAdapter> Find() =>
        LanAdapters.Find().Select(adapter => new WindowsLanAdapter(adapter.Name, adapter.Address)).ToList();
}
