using System.Text.Json;
using PhoneTransfer.Host;
using Xunit;

namespace PhoneTransfer.Tests;

public sealed class ShareConfigurationTests : IDisposable
{
    private readonly string rootDirectory;
    private readonly string dataDirectory;
    private readonly string shareDirectory;

    public ShareConfigurationTests()
    {
        rootDirectory = Path.Combine(Path.GetTempPath(), "PhoneTransferShareTests", Guid.NewGuid().ToString("N"));
        dataDirectory = Path.Combine(rootDirectory, "data");
        shareDirectory = Path.Combine(rootDirectory, "share");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(shareDirectory);
    }

    [Fact]
    public void PersistsReadsAndClearsShareRoot()
    {
        var first = new WindowsShareConfiguration(dataDirectory);
        var expected = Path.GetFullPath(shareDirectory);

        Assert.Equal(expected, first.SetRootPath(shareDirectory));
        Assert.Equal(expected, new WindowsShareConfiguration(dataDirectory).GetRootPath());

        first.Clear();
        Assert.Null(new WindowsShareConfiguration(dataDirectory).GetRootPath());
    }

    [Fact]
    public void RejectsUnsafeOrUnavailableRootsBeforePersistence()
    {
        var configuration = new WindowsShareConfiguration(dataDirectory);

        Assert.Throws<ArgumentException>(() => configuration.SetRootPath(@"\\server\share"));
        Assert.Throws<ArgumentException>(() => configuration.SetRootPath(Path.GetPathRoot(shareDirectory)!));
        Assert.Throws<ArgumentException>(() => configuration.SetRootPath(dataDirectory));
        Assert.Throws<DirectoryNotFoundException>(() => configuration.SetRootPath(Path.Combine(rootDirectory, "missing")));
        Assert.Null(configuration.GetRootPath());
    }

    [Fact]
    public void RejectsUnknownConfigurationVersionInsteadOfSilentlyResetting()
    {
        var payload = JsonSerializer.Serialize(new { version = 2, rootPath = shareDirectory });
        File.WriteAllText(Path.Combine(dataDirectory, "share-config.json"), payload);

        Assert.Throws<InvalidDataException>(() => new WindowsShareConfiguration(dataDirectory).GetRootPath());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(rootDirectory)) Directory.Delete(rootDirectory, true);
        }
        catch (IOException)
        {
            // Temporary test cleanup is best-effort on Windows CI.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary test cleanup is best-effort on Windows CI.
        }
    }
}
