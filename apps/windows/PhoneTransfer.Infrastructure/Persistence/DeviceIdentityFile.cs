using PhoneTransfer.Application;

namespace PhoneTransfer.Infrastructure.Persistence;

public sealed class DeviceIdentityFile : IServerIdentity
{
    public Guid DeviceId { get; }
    public string DisplayName => Environment.MachineName;

    public DeviceIdentityFile(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "device-id");
        if (File.Exists(path))
        {
            // Corruption must not silently replace a paired identity.
            DeviceId = Guid.Parse(File.ReadAllText(path).Trim());
            return;
        }
        var id = Guid.NewGuid();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(id.ToString("D"));
        writer.Flush();
        stream.Flush(flushToDisk: true);
        DeviceId = id;
    }
}
