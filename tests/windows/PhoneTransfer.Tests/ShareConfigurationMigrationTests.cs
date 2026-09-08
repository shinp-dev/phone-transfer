using System.Runtime.Versioning;
using System.Text.Json;
using PhoneTransfer.Infrastructure.Storage;
using Xunit;

namespace PhoneTransfer.Tests;

[SupportedOSPlatform("windows")]
public sealed class ShareConfigurationMigrationTests : IDisposable
{
    private readonly string rootDirectory = Path.Combine(Path.GetTempPath(), "PhoneTransferShareMigration", Guid.NewGuid().ToString("N"));
    private string DataDirectory => Path.Combine(rootDirectory, "data");
    private string ShareDirectory => Path.Combine(rootDirectory, "share");

    public ShareConfigurationMigrationTests()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ShareDirectory);
    }

    [Fact]
    public void LegacyVersionOneConfigurationIsAtomicallyUpgradedOnceAndKeepsItsGeneration()
    {
        var path = Path.Combine(DataDirectory, "share-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, rootPath = ShareDirectory }));

        var first = new WindowsShareConfigurationStore(DataDirectory).Read();
        Assert.NotNull(first);
        Assert.NotEqual(Guid.Empty, first!.Generation);
        Assert.Equal(Path.GetFullPath(ShareDirectory), first.RootPath);

        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(first.Generation, document.RootElement.GetProperty("generation").GetGuid());
        }

        var second = new WindowsShareConfigurationStore(DataDirectory).Read();
        Assert.Equal(first, second);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootDirectory)) Directory.Delete(rootDirectory, true);
    }
}
