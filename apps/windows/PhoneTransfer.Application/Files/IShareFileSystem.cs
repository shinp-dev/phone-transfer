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
public sealed record ShareEntry(string Name, bool IsDirectory);

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
