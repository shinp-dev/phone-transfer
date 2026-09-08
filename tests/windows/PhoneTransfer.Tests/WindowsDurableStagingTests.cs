using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Domain;
using PhoneTransfer.Infrastructure.Persistence;
using PhoneTransfer.Infrastructure.Storage;
using Xunit;

namespace PhoneTransfer.Tests;

public sealed partial class WindowsShareFileSystemTests
{
    private IDurableShareSession DurableOpen() => new WindowsShareFileSystem().OpenDurable(new ShareConfiguration(Root));
    private StagingCapability PersistStaging()
    {
        using var session = DurableOpen();
        using var staging = session.CreateDurable(Relative("recovered.bin"));
        staging.Write(0, "committed-tail"u8);
        staging.Flush();
        return staging.Capability;
    }
    // Test attacker/operator paths only. Production recovery never reconstructs these paths.
    private string PrivateDirectory(StagingCapability capability) => Path.Combine(Root, ".phone-transfer-staging-durable-" + capability.DirectoryToken.ToString("N"));
    private string PrivateFile(StagingCapability capability) => Path.Combine(PrivateDirectory(capability), capability.FileToken.ToString("N") + ".part");

    [Fact]
    public void DurableReopenTruncatesThroughStableHandleAndDisposePreservesCommittedPrefix()
    {
        var capability = PersistStaging();
        for (var restart = 0; restart < 2; restart++)
        {
            using var session = DurableOpen();
            using var staging = session.Reopen(capability, Relative("recovered.bin"));
            Assert.Equal(capability, staging.Capability);
            staging.Truncate(9);
            Assert.Equal(9, staging.Length);
            Assert.Throws<IOException>(() => staging.Truncate(10));
        }
        using (var listing = Open()) Assert.Empty(listing.List(Relative("")));
        Assert.Equal("committed", File.ReadAllText(PrivateFile(capability)));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("hardlink")]
    [InlineData("symlink")]
    public void CrashReopenRejectsStagingReplacementWithoutTouchingOutside(string replacement)
    {
        var capability = PersistStaging();
        var file = PrivateFile(capability);
        // Keep the original object alive under another name to make different file ID deterministic.
        File.Move(file, Path.Combine(PrivateDirectory(capability), "original.part"));
        if (replacement == "file") File.WriteAllText(file, "committed-tail");
        else if (replacement == "symlink") File.CreateSymbolicLink(file, Path.Combine(Outside, "secret.txt"));
        else Assert.True(CreateHardLinkW(file, Path.Combine(Outside, "secret.txt"), IntPtr.Zero), Marshal.GetLastWin32Error().ToString());
        using var session = DurableOpen();
        Assert.Throws<IOException>(() => session.Reopen(capability, Relative("recovered.bin")));
    }

    [Fact]
    public void CrashReopenRejectsPrivateDirectoryJunctionAndOrphanCleanupDoesNotFollowIt()
    {
        var capability = PersistStaging();
        var original = PrivateDirectory(capability);
        Directory.Move(original, original + "-kept");
        Junction(original, Outside);
        using var session = DurableOpen();
        Assert.Throws<IOException>(() => session.Reopen(capability, Relative("recovered.bin")));
        session.CleanupOrphans(new HashSet<Guid>());
        Assert.True(Directory.Exists(original));
    }

    [Fact]
    public void CrashReopenRejectsChangedRootEvenAtIdenticalConfiguredPath()
    {
        var capability = PersistStaging();
        Directory.Move(Root, Path.Combine(fixture, "old-share"));
        Directory.CreateDirectory(Root);
        using var session = DurableOpen();
        Assert.Throws<IOException>(() => session.Reopen(capability, Relative("recovered.bin")));
    }

    [Fact]
    public void CrashReopenRejectsStaleCapabilityAndReleasesPartialTraversalHandles()
    {
        var capability = PersistStaging();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var session = DurableOpen();
            Assert.Throws<IOException>(() => session.Reopen(capability with { FileToken = Guid.NewGuid() }, Relative("recovered.bin")));
            Assert.Throws<IOException>(() => session.Reopen(capability with { FileIdentity = new string('f', 48) }, Relative("recovered.bin")));
        }
        File.Move(PrivateFile(capability), PrivateFile(capability) + ".moved"); // Fails if any reopened handle leaked.
    }

    [Fact]
    public void PrivateAclIsValidatedOnReopenRatherThanAssumedFromCreation()
    {
        var capability = PersistStaging();
        var directory = new DirectoryInfo(PrivateDirectory(capability));
        var acl = directory.GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        acl.SetAccessRuleProtection(false, false);
        directory.SetAccessControl(acl);
        using var session = DurableOpen();
        Assert.Throws<IOException>(() => session.Reopen(capability, Relative("recovered.bin")));
        session.CleanupOrphans(new HashSet<Guid>());
        Assert.True(File.Exists(PrivateFile(capability)));
    }

    [Fact]
    public void OrphanCleanupDeletesOnlyUnreferencedValidatedPrivateObjects()
    {
        var referenced = PersistStaging();
        var orphan = PersistStaging();
        using (var session = DurableOpen()) session.CleanupOrphans(new HashSet<Guid> { referenced.DirectoryToken });
        Assert.True(File.Exists(PrivateFile(referenced)));
        Assert.False(Directory.Exists(PrivateDirectory(orphan)));
        using (var session = DurableOpen()) session.CleanupOrphans(new HashSet<Guid> { referenced.DirectoryToken });
        Assert.True(File.Exists(PrivateFile(referenced)));
    }

    [Fact]
    public void ReopenedStagingCannotOverwriteDestinationCreatedBeforeRename()
    {
        var capability = PersistStaging();
        using var session = DurableOpen();
        using var staging = session.Reopen(capability, Relative("recovered.bin"));
        File.WriteAllText(Path.Combine(Root, "recovered.bin"), "competitor");
        Assert.Throws<IOException>(() => staging.CompleteNoReplace());
        Assert.Equal(14, staging.Length);
        Assert.Equal("competitor", File.ReadAllText(Path.Combine(Root, "recovered.bin")));
    }

    [Fact]
    public void NativeRenameCrashIsProvenByFileIdSizeAndHashWithNewSqliteAndServiceInstances()
    {
        var configuration = new WindowsShareConfigurationStore(Path.Combine(fixture, "data"));
        configuration.Save(Root);
        var now = DateTimeOffset.UtcNow;
        var device = new PairedDevice(Guid.NewGuid(), "phone", new string('a', 64), now, now, DevicePermissions.All, false);
        var database = Path.Combine(fixture, "data", "transfers.db");
        var fileSystem = new WindowsShareFileSystem();
        var bytes = "native durable recovery"u8.ToArray();
        Guid transferId;
        using (var service = new DurableFileTransferService(configuration, fileSystem, fileSystem,
                   new SqliteTransferJournal(database), _ => device,
                   point => { if (point == TransferFaultPoint.Renamed) throw new NativeCrash(); }))
        {
            service.Initialize();
            var share = Assert.Single(service.ListShares(device));
            transferId = service.CreateTransfer(device, share.Id, Relative("native.bin"), bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), Guid.NewGuid()).TransferId;
            service.Append(device, transferId, 0, bytes);
            Assert.Throws<NativeCrash>(() => service.Complete(device, transferId));
        }
        for (var restart = 0; restart < 2; restart++)
        {
            using var service = new DurableFileTransferService(configuration, fileSystem, fileSystem,
                new SqliteTransferJournal(database), _ => device);
            service.Initialize();
            Assert.Equal(TransferState.Completed, service.GetTransfer(device, transferId).State);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Root, "native.bin")));
        }
    }

    private sealed class NativeCrash : Exception;
}
