using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Domain;
using PhoneTransfer.Infrastructure.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PhoneTransfer.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsShareFileSystemTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PhoneTransferHandles", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper output;
    private string Root => Path.Combine(fixture, "share");
    private string Outside => Path.Combine(fixture, "outside");
    private static RelativeSharePath Relative(string value) => RelativeSharePath.Parse(value, allowRoot: true);
    private IShareFileSession Open() => new WindowsShareFileSystem().Open(new ShareConfiguration(Root));

    public WindowsShareFileSystemTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "secret.txt"), "outside-secret");
    }

    [Fact]
    public void DeepNfcReadAndListingUseLiveHandles()
    {
        Directory.CreateDirectory(Path.Combine(Root, "深い", "café", "leaf"));
        File.WriteAllText(Path.Combine(Root, "深い", "café", "leaf", "資料.txt"), "safe");
        using var session = Open();
        using var file = session.OpenRead(Relative("深い/café/leaf/資料.txt"));
        Assert.Equal(4, file.Length);
        var bytes = new byte[16];
        Assert.Equal(4, file.Read(0, bytes));
        Assert.Equal("safe", Encoding.UTF8.GetString(bytes, 0, 4));
        Assert.Equal(new ShareEntry("資料.txt", false), Assert.Single(session.List(Relative("深い/café/leaf"))));
        Assert.Single(session.List(Relative(""), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.List(Relative(""), 201));
    }

    [Theory]
    [InlineData("../outside/secret.txt")]
    [InlineData("a/../secret.txt")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("\\\\server\\share")]
    [InlineData("name:stream")]
    [InlineData("name::$DATA")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("%2e%2e/secret")]
    [InlineData("cafe\u0301.txt")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("COM¹.txt")]
    [InlineData("LPT9")]
    public void SyntaxBoundaryRemainsMandatory(string input)
    {
        Assert.Throws<ArgumentException>(() => Relative(input));
    }

    [Theory]
    [InlineData("\\\\server\\share")]
    [InlineData("C:relative")]
    [InlineData("C:\\")]
    [InlineData("\\\\?\\C:\\share")]
    public void AdapterRejectsUnsupportedRootSyntax(string root)
    {
        Assert.Throws<ArgumentException>(() => new WindowsShareFileSystem().Open(new ShareConfiguration(root)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsJunctionAtLeafOrDuringTraversal(bool intermediate)
    {
        Junction(Path.Combine(Root, "link"), Outside);
        using var session = Open();
        Assert.Throws<IOException>(() => session.List(Relative(intermediate ? "link/subdirectory" : "link")));
        Assert.Throws<IOException>(() => session.OpenRead(Relative("link/secret.txt")));
        Assert.Throws<IOException>(() => session.CreateStaging(Relative("link/new.txt")));
        Assert.Empty(session.List(Relative("")));
        Assert.Equal("outside-secret", File.ReadAllText(Path.Combine(Outside, "secret.txt")));
    }

    [Fact]
    public void RejectsFileAndDirectorySymbolicLinks()
    {
        // Mandatory on windows-latest. Privilege failures fail visibly, never silently skip coverage.
        File.CreateSymbolicLink(Path.Combine(Root, "link.txt"), Path.Combine(Outside, "secret.txt"));
        Directory.CreateSymbolicLink(Path.Combine(Root, "linkdir"), Outside);
        using var session = Open();
        Assert.Throws<IOException>(() => session.OpenRead(Relative("link.txt")));
        Assert.Throws<IOException>(() => session.List(Relative("linkdir")));
        using var staging = session.CreateStaging(Relative("link.txt"));
        Assert.Throws<IOException>(() => staging.DestinationExists());
        Assert.Throws<IOException>(() => staging.CompleteNoReplace());
        Assert.Empty(session.List(Relative("")));
        Assert.Equal("outside-secret", File.ReadAllText(Path.Combine(Outside, "secret.txt")));
    }

    [Fact]
    public void RejectsHardLinkToExternalFile()
    {
        Assert.True(CreateHardLinkW(Path.Combine(Root, "hard.txt"), Path.Combine(Outside, "secret.txt"), IntPtr.Zero),
            $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
        using var session = Open();
        Assert.Throws<IOException>(() => session.OpenRead(Relative("hard.txt")));
        Assert.Empty(session.List(Relative("")));
    }

    [Fact]
    public void RevalidatesRootAfterConfigurationWasSaved()
    {
        var store = new WindowsShareConfigurationStore(Path.Combine(fixture, "state"));
        var saved = store.Save(Root);
        Directory.Move(Root, Root + "-original");
        Junction(Root, Outside);
        Assert.Throws<IOException>(() => new WindowsShareFileSystem().Open(saved));
        Assert.Throws<NotSupportedException>(() => store.Read());
    }

    [Fact]
    public void RejectsRootWithJunctionInItsAncestry()
    {
        Directory.CreateDirectory(Path.Combine(Outside, "nested"));
        Junction(Path.Combine(Root, "link"), Outside);
        Assert.Throws<IOException>(() => new WindowsShareFileSystem().Open(
            new ShareConfiguration(Path.Combine(Root, "link", "nested"))));
    }

    [Fact]
    public void RootAndAllAncestorsStayPinnedUntilSessionDisposal()
    {
        using (var session = Open())
        {
            Assert.Throws<IOException>(() => Directory.Move(Root, Root + "-moved"));
            Assert.Throws<IOException>(() => Directory.Move(fixture, fixture + "-moved"));
            Assert.Throws<IOException>(() => Directory.Delete(Root));
        }
        Directory.Move(Root, Root + "-moved");
        Directory.Move(Root + "-moved", Root);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x100u)] // FILE_WRITE_ATTRIBUTES does not participate in data sharing checks.
    [InlineData(0x40000000u)] // GENERIC_WRITE
    public void PinnedRootFailsClosedOnInPlaceReparseMutation(uint access)
    {
        using var session = Open();
        using var attacker = CreateFileW(Root, access, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (attacker.IsInvalid)
        {
            Assert.Contains(Marshal.GetLastWin32Error(), new[] { 5, 32 });
            return;
        }
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Outside);
        var print = Encoding.Unicode.GetBytes(Outside);
        var data = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0); // IO_REPARSE_TAG_MOUNT_POINT
        BitConverter.GetBytes(checked((ushort)(data.Length - 8))).CopyTo(data, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(data, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(data, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(data, 14);
        substitute.CopyTo(data, 16);
        print.CopyTo(data, 18 + substitute.Length);
        // Positive control: the exact same buffer must really create a junction on an unpinned
        // directory. A malformed attack payload must not make the rejection test pass.
        var controlPath = Path.Combine(fixture, "mutation-control");
        Directory.CreateDirectory(controlPath);
        using (var control = CreateFileW(controlPath, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            Assert.False(control.IsInvalid);
            Assert.True(DeviceIoControl(control, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
                $"Control mutation failed: {Marshal.GetLastWin32Error()}");
        }
        Assert.True((File.GetAttributes(controlPath) & FileAttributes.ReparsePoint) != 0);
        var changed = DeviceIoControl(attacker, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
        output.WriteLine($"In-place mutation access={access:X}: changed={changed}, error={Marshal.GetLastWin32Error()}");
        if (changed)
        {
            Assert.Throws<IOException>(() => session.List(Relative("")));
            Assert.Throws<IOException>(() => session.CreateStaging(Relative("must-not-exist.txt")));
            Assert.Single(Directory.GetFileSystemEntries(Outside));
        }
        else Assert.Empty(session.List(Relative("")));
    }

    [Fact]
    public void CheckUseBoundaryIsDeterministicAndCannotSwapOpenReadOrAncestors()
    {
        var middle = Path.Combine(Root, "middle");
        var leaf = Path.Combine(middle, "leaf");
        Directory.CreateDirectory(leaf);
        var path = Path.Combine(leaf, "data.txt");
        File.WriteAllText(path, "original");
        using var session = Open();
        using (var file = session.OpenRead(Relative("middle/leaf/data.txt")))
        {
            // OpenRead has finished native open + validation, but no content has been used yet.
            Assert.Throws<IOException>(() => File.Move(path, path + ".old"));
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => File.WriteAllText(path, "changed"));
            Assert.Throws<IOException>(() => Directory.Move(middle, middle + "-old"));
            Assert.Throws<IOException>(() => Directory.Move(leaf, leaf + "-old"));
            var bytes = new byte[8];
            Assert.Equal(8, file.Read(0, bytes));
            Assert.Equal("original", Encoding.UTF8.GetString(bytes));
        }
        Directory.Move(middle, middle + "-old");
        Junction(middle, Outside);
        Assert.Throws<IOException>(() => session.OpenRead(Relative("middle/secret.txt")));
    }

    [Fact]
    public void ExistingWriteOrDeleteHandlePreventsValidationFromSucceeding()
    {
        var path = Path.Combine(Root, "busy.txt");
        File.WriteAllText(path, "original");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var session = Open();
        Assert.Throws<IOException>(() => session.OpenRead(Relative("busy.txt")));
    }

    [Fact]
    public void CompletionIsAtomicNoReplaceEvenWhenDestinationAppearsAfterCheck()
    {
        Directory.CreateDirectory(Path.Combine(Root, "deep", "leaf"));
        var destination = Path.Combine(Root, "deep", "leaf", "final.txt");
        using var session = Open();
        using var staging = session.CreateStaging(Relative("deep/leaf/final.txt"));
        staging.Write(0, "uploaded"u8);
        staging.Flush();
        Assert.False(staging.DestinationExists());
        Assert.Throws<IOException>(() => Directory.Move(Path.Combine(Root, "deep"), Path.Combine(Root, "old")));
        File.WriteAllText(destination, "winner"); // Deterministically between existence check and rename.
        Assert.True(staging.DestinationExists());
        Assert.Throws<IOException>(() => staging.CompleteNoReplace());
        Assert.Equal("winner", File.ReadAllText(destination));
        Assert.Equal(8, staging.Length); // A conflict retains the original staging handle for retry.
        File.Delete(destination);
        staging.CompleteNoReplace();
        Assert.Throws<ObjectDisposedException>(() => staging.Flush());
        Assert.Equal("uploaded", File.ReadAllText(destination));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("空")]
    public void EmptyFileCompletesWithSingleCharacterDestination(string name)
    {
        using var session = Open();
        using var staging = session.CreateStaging(Relative(name));
        staging.CompleteNoReplace();
        using var file = session.OpenRead(Relative(name));
        Assert.Equal(0, file.Length);
        Assert.Equal(0, file.Read(0, new byte[1]));
    }

    [Fact]
    public void CompletionCannotReplaceDirectoryOrJunction()
    {
        Junction(Path.Combine(Root, "target"), Outside);
        using var session = Open();
        using var staging = session.CreateStaging(Relative("target"));
        Assert.Throws<IOException>(() => staging.CompleteNoReplace());
        Assert.True((File.GetAttributes(Path.Combine(Root, "target")) & FileAttributes.ReparsePoint) != 0);
    }

    [Fact]
    public void PrivateStagingHasProtectedCurrentUserAclAndIsInaccessibleByNameOrAlias()
    {
        using var session = Open();
        using var staging = session.CreateStaging(Relative("final.txt"));
        var directory = Assert.Single(Directory.GetDirectories(Root));
        // Read ACL through the filesystem after closing neither owned object: READ_CONTROL doesn't
        // conflict with the data/delete sharing lock. ACL creation is atomic, not create-then-harden.
        var security = new DirectoryInfo(directory).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        using var identity = WindowsIdentity.GetCurrent();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        var rule = Assert.Single(rules);
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.False(rule.IsInherited);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        Assert.Empty(session.List(Relative("")));
        var name = Path.GetFileName(directory);
        Assert.Throws<ArgumentException>(() => session.List(Relative(name)));
        Assert.Throws<ArgumentException>(() => session.OpenRead(Relative(name + "/anything.part")));
        Assert.Throws<ArgumentException>(() => session.CreateStaging(Relative(name + "/new")));
        var shortName = new StringBuilder(32768);
        var length = GetShortPathNameW(directory, shortName, (uint)shortName.Capacity);
        Assert.True(length > 0 && length < shortName.Capacity);
        var alias = Path.GetFileName(shortName.ToString());
        if (!string.Equals(alias, name, StringComparison.OrdinalIgnoreCase))
            Assert.ThrowsAny<Exception>(() => session.List(Relative(alias)));
        else output.WriteLine("8.3 alias not present on this volume; canonical private-name rejection asserted.");
    }

    [Fact]
    public void DisposalClosesOutstandingChildrenAndRemovesStaging()
    {
        var session = Open();
        var readPath = Path.Combine(Root, "read.txt");
        File.WriteAllText(readPath, "read");
        var read = session.OpenRead(Relative("read.txt"));
        var staging = session.CreateStaging(Relative("final.txt"));
        staging.Write(0, "abandoned"u8);
        session.Dispose();
        session.Dispose();
        read.Dispose();
        staging.Dispose();
        Assert.Throws<ObjectDisposedException>(() => read.Length);
        Assert.Throws<ObjectDisposedException>(() => staging.Flush());
        Assert.Empty(Directory.GetDirectories(Root));
        Assert.False(File.Exists(Path.Combine(Root, "final.txt")));
        File.Delete(readPath);
        Directory.Delete(Root); // A leaked read/root handle would make this fail.
    }

    [Fact]
    public void FailedTraversalsAndRepeatedOpenDisposeReleaseEveryHandle()
    {
        Directory.CreateDirectory(Path.Combine(Root, "deep"));
        File.WriteAllText(Path.Combine(Root, "deep", "read.txt"), "read");
        Junction(Path.Combine(Root, "deep", "link"), Outside);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var session = Open();
            Assert.Throws<IOException>(() => session.OpenRead(Relative("deep/link/secret.txt")));
            Assert.Throws<IOException>(() => session.CreateStaging(Relative("deep/missing/file.txt")));
            using var file = session.OpenRead(Relative("deep/read.txt"));
        }
        DeleteTree(Root); // Includes parents retained on partial-failure paths.
    }

    private static void Junction(string path, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{path}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10000), "mklink timed out");
        Assert.True(process.ExitCode == 0, stdout + stderr);
    }

    private static void DeleteTree(string path)
    {
        // .NET recursive removal calls DeleteVolumeMountPoint for junctions, which can return
        // ERROR_INVALID_PARAMETER on the runner. Remove the link itself without traversing it.
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var directory in Directory.GetDirectories(path)) DeleteTree(directory);
            foreach (var file in Directory.GetFiles(path)) File.Delete(file);
        }
        Assert.True(RemoveDirectoryW(path), $"RemoveDirectory failed: {Marshal.GetLastWin32Error()}");
    }

    public void Dispose()
    {
        // Deliberately not best-effort: leaked handles or accidental outside-file damage fail the test.
        Assert.Equal("outside-secret", File.ReadAllText(Path.Combine(Outside, "secret.txt")));
        DeleteTree(fixture);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectoryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int inputSize,
        IntPtr output, int outputSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);
}
