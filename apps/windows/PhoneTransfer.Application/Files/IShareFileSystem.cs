using PhoneTransfer.Domain;

namespace PhoneTransfer.Application.Files;

// Local configuration only. Never construct ShareConfiguration from a network request.
public interface IShareFileSystem
{
    IShareFileSession Open(ShareConfiguration configuration);
}

// A session pins the root. Disposal invalidates and closes all outstanding children.
// Operations are serialized, including child I/O and disposal. No OS paths/handles escape.
public interface IShareFileSession : IDisposable
{
    IReadOnlyList<ShareEntry> List(RelativeSharePath directory, int limit = 200);
    IShareReadFile OpenRead(RelativeSharePath file);
    IShareStagingFile CreateStaging(RelativeSharePath destination);
}

// Listing is advisory, bounded and not a snapshot or an authorization capability.
// Equality intentionally remains name/kind based for the pre-metadata application contract.
public sealed class ShareEntry : IEquatable<ShareEntry>
{
    public ShareEntry(string name, bool isDirectory, long size = 0, DateTimeOffset modifiedAt = default)
    {
        Name = name;
        IsDirectory = isDirectory;
        Size = size;
        ModifiedAt = modifiedAt;
    }

    public string Name { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
    public DateTimeOffset ModifiedAt { get; }

    public bool Equals(ShareEntry? other) => other is not null &&
        string.Equals(Name, other.Name, StringComparison.Ordinal) && IsDirectory == other.IsDirectory;

    public override bool Equals(object? obj) => Equals(obj as ShareEntry);
    public override int GetHashCode() => HashCode.Combine(Name, IsDirectory);
}

public interface IShareReadFile : IDisposable
{
    long Length { get; }
    int Read(long offset, Span<byte> buffer);
}

public interface IShareStagingFile : IShareReadFile
{
    void Write(long offset, ReadOnlySpan<byte> buffer);
    void Flush();
    // Advisory only. CompleteNoReplace is the atomic authority even after a false result.
    bool DestinationExists();
    // Flush and same-volume rename without overwrite. Success invalidates this object.
    // Failure retains staging for retry. Disposal abandons and deletes uncompleted bytes.
    void CompleteNoReplace();
}
