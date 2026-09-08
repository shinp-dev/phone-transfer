using System.Runtime.Versioning;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Infrastructure.Storage;

namespace PhoneTransfer.Host;

[SupportedOSPlatform("windows")]
public sealed class WindowsShareConfiguration
{
    private readonly IShareConfigurationStore store;

    public WindowsShareConfiguration(string directory)
    {
        store = new WindowsShareConfigurationStore(directory);
    }

    public string? GetRootPath() => store.Read()?.RootPath;

    public string SetRootPath(string rootPath) => store.Save(rootPath).RootPath;

    public void Clear() => store.Clear();
}
