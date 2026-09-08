using System.Text.Json;
using System.Text.Json.Serialization;
using PhoneTransfer.Application.Files;

namespace PhoneTransfer.Infrastructure.Storage;

public sealed class WindowsShareConfigurationStore : IShareConfigurationStore
{
    private const long MaxConfigurationBytes = 8 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string dataDirectory;
    private readonly string configurationPath;

    public WindowsShareConfigurationStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        configurationPath = Path.Combine(this.dataDirectory, "share-config.json");
    }

    public ShareConfiguration? Read()
    {
        if (!File.Exists(configurationPath)) return null;
        if (new FileInfo(configurationPath).Length > MaxConfigurationBytes)
            throw new InvalidDataException("SHARE_CONFIGURATION_TOO_LARGE");

        PersistedShareConfiguration? persisted;
        try
        {
            using var stream = new FileStream(configurationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            persisted = JsonSerializer.Deserialize<PersistedShareConfiguration>(stream, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("SHARE_CONFIGURATION_INVALID", exception);
        }

        if (persisted is null || persisted.Version != 1 || string.IsNullOrWhiteSpace(persisted.RootPath))
            throw new InvalidDataException("SHARE_CONFIGURATION_INVALID");

        return new ShareConfiguration(ValidateRoot(persisted.RootPath));
    }

    public ShareConfiguration Save(string rootPath)
    {
        var normalized = ValidateRoot(rootPath);
        Directory.CreateDirectory(dataDirectory);
        var temporaryPath = configurationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new PersistedShareConfiguration(1, normalized), JsonOptions);
                stream.Flush(true);
            }

            if (File.Exists(configurationPath)) File.Replace(temporaryPath, configurationPath, null);
            else File.Move(temporaryPath, configurationPath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A stale configuration temp file is harmless and will never be read as active settings.
            }
        }

        return new ShareConfiguration(normalized);
    }

    public void Clear()
    {
        if (File.Exists(configurationPath)) File.Delete(configurationPath);
    }

    private string ValidateRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var fullPath = Path.GetFullPath(rootPath);
        if (!Path.IsPathFullyQualified(fullPath) ||
            fullPath.Length < 3 ||
            !char.IsAsciiLetter(fullPath[0]) ||
            fullPath[1] != ':' ||
            (fullPath[2] != '\\' && fullPath[2] != '/'))
            throw new ArgumentException("SHARE_ROOT_MUST_BE_LOCAL_DRIVE", nameof(rootPath));

        var driveRoot = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("SHARE_ROOT_MUST_BE_LOCAL_DRIVE", nameof(rootPath));
        var normalized = string.Equals(fullPath, driveRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);

        if (string.Equals(normalized, driveRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SHARE_ROOT_VOLUME_ROOT_NOT_ALLOWED", nameof(rootPath));
        if (!Directory.Exists(normalized))
            throw new DirectoryNotFoundException("SHARE_ROOT_NOT_FOUND");
        if (PathsOverlap(normalized, dataDirectory))
            throw new ArgumentException("SHARE_ROOT_OVERLAPS_APP_DATA", nameof(rootPath));

        var drive = new DriveInfo(driveRoot);
        if (!drive.IsReady) throw new IOException("SHARE_ROOT_DRIVE_NOT_READY");
        if (drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.Ram or
            DriveType.NoRootDirectory or DriveType.Unknown)
            throw new NotSupportedException("SHARE_ROOT_DRIVE_NOT_LOCAL");
        if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(drive.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("SHARE_ROOT_FILESYSTEM_UNSUPPORTED");

        RejectReparseAncestors(normalized, driveRoot);
        return normalized;
    }

    private static void RejectReparseAncestors(string path, string driveRoot)
    {
        var current = new DirectoryInfo(path);
        while (!string.Equals(current.FullName, driveRoot, StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("SHARE_ROOT_REPARSE_POINT");
            current = current.Parent ?? throw new IOException("SHARE_ROOT_PARENT_UNAVAILABLE");
        }
    }

    private static bool PathsOverlap(string first, string second) =>
        ContainsPath(first, second) || ContainsPath(second, first);

    private static bool ContainsPath(string parent, string child)
    {
        if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = Path.EndsInDirectorySeparator(parent)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PersistedShareConfiguration(int Version, string RootPath);
}
