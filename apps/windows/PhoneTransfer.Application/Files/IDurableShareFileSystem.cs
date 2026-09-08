using PhoneTransfer.Domain;

namespace PhoneTransfer.Application.Files;

// Local-only capabilities. No OS path, caller-selected token or native handle crosses this boundary.
public sealed record StagingCapability(Guid DirectoryToken, Guid FileToken, string DirectoryIdentity,
    string FileIdentity, string RootIdentity, string DestinationParentIdentity);

public interface IDurableShareFileSystem
{
    IDurableShareSession OpenDurable(ShareConfiguration configuration);
}

public interface IDurableShareSession : IDisposable
{
    string RootIdentity { get; }
    IDurableStagingFile CreateDurable(RelativeSharePath destination);
    IDurableStagingFile Reopen(StagingCapability capability, RelativeSharePath destination);
    bool VerifyDestination(StagingCapability capability, RelativeSharePath destination, long size, string sha256);
    // Only current-root private objects, bounded enumeration, never follows unknown/unsafe objects.
    void CleanupOrphans(IReadOnlySet<Guid> referencedDirectories);
}

public interface IDurableStagingFile : IShareReadFile
{
    StagingCapability Capability { get; }
    void Write(long offset, ReadOnlySpan<byte> buffer);
    void Flush();
    // Includes FlushToDisk; successful return proves rollback durability.
    void Truncate(long committedOffset);
    void CompleteNoReplace();
    void Delete();
    // Dispose only closes handles. It MUST NOT delete persistent bytes.
}
