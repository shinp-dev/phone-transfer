using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhoneTransfer.Infrastructure.Storage;

[SupportedOSPlatform("windows")]
internal static class WindowsFileNative
{
    internal const uint ReadAccess = 0x00100081; // SYNCHRONIZE | READ_ATTRIBUTES | READ_DATA/LIST_DIRECTORY
    internal const uint DeleteAccess = 0x00010000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint SynchronousIo = 0x00000020;

    internal static IOException Error(int code) => new("FILESYSTEM_OPERATION_FAILED", new Win32Exception(code));

    internal static SafeFileHandle OpenVolume(string drive)
    {
        var handle = CreateFileW(drive, ReadAccess, 3, IntPtr.Zero, 3,
            0x02000000 | OpenReparsePoint, IntPtr.Zero); // BACKUP_SEMANTICS; no delete sharing
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw Error(error);
        }
        return handle;
    }

    // Single component only; no native call ever receives a caller-controlled full child path.
    // Caller owns the parent under the session gate. DangerousAddRef also pins embedded handles.
    internal static SafeFileHandle OpenChild(SafeFileHandle parent, string name, bool? directory,
        bool create = false, bool writable = false, bool delete = false, bool privateAcl = false)
    {
        if (name.Length is 0 or > 255 || name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
            throw new ArgumentException("INVALID_NATIVE_COMPONENT", nameof(name));
        var text = Marshal.StringToHGlobalUni(name);
        var unicodeMemory = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        IntPtr descriptor = IntPtr.Zero;
        var pinned = false;
        try
        {
            if (privateAcl)
            {
                using var identity = WindowsIdentity.GetCurrent();
                var sid = identity.User?.Value ?? throw new IOException("CURRENT_USER_UNAVAILABLE");
                // Protected DACL, no inherited ACEs; applied atomically at FILE_CREATE.
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                        $"O:{sid}D:P(A;OICI;FA;;;{sid})", 1, out descriptor, out _))
                    throw Error(Marshal.GetLastWin32Error());
            }
            Marshal.StructureToPtr(new UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                Buffer = text
            }, unicodeMemory, false);
            parent.DangerousAddRef(ref pinned);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeMemory,
                Attributes = 0x1040, // OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE
                SecurityDescriptor = descriptor
            };
            var access = ReadAccess | (writable ? 2u : 0) | (delete ? DeleteAccess : 0);
            var options = OpenReparsePoint | SynchronousIo | (directory == true ? 1u : directory == false ? 0x40u : 0);
            var status = NtCreateFile(out var result, access, ref attributes, out _, IntPtr.Zero,
                0x80, directory == true ? 3u : writable || delete ? 0u : 1u, create ? 2u : 1u, options, IntPtr.Zero, 0);
            if (status < 0)
            {
                result.Dispose();
                throw Error(unchecked((int)RtlNtStatusToDosError(status)));
            }
            return result;
        }
        finally
        {
            if (pinned) parent.DangerousRelease();
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            Marshal.FreeHGlobal(unicodeMemory);
            Marshal.FreeHGlobal(text);
        }
    }

    internal static bool IsMissing(IOException exception) =>
        exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 };

    internal static (string Path, ulong Volume, bool Directory, long Length, DateTimeOffset ModifiedAt) Inspect(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(40);
        try
        {
            if (!GetFileInformationByHandleEx(handle, 9, buffer, 8)) throw Error(Marshal.GetLastWin32Error());
            var attributes = unchecked((uint)Marshal.ReadInt32(buffer));
            if ((attributes & 0x400) != 0) throw new IOException("REPARSE_POINT_REJECTED");
            var directory = (attributes & 0x10) != 0;
            if (!GetFileInformationByHandleEx(handle, 1, buffer, 24)) throw Error(Marshal.GetLastWin32Error());
            var length = directory ? 0 : Marshal.ReadInt64(buffer, 8);
            // Reject preexisting hard links as well as pending deletion. File writes/deletes are not shared.
            if (Marshal.ReadByte(buffer, 20) != 0 || (!directory && Marshal.ReadInt32(buffer, 16) != 1))
                throw new IOException("UNSTABLE_FILE_REJECTED");
            if (!GetFileInformationByHandleEx(handle, 0, buffer, 40)) throw Error(Marshal.GetLastWin32Error());
            var modifiedAt = DateTimeOffset.FromFileTime(Marshal.ReadInt64(buffer, 16));
            if (!GetFileInformationByHandleEx(handle, 18, buffer, 24)) throw Error(Marshal.GetLastWin32Error());
            var volume = unchecked((ulong)Marshal.ReadInt64(buffer));
            var path = new StringBuilder(32768);
            var pathLength = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 1); // VOLUME_NAME_GUID, normalized
            if (pathLength == 0) throw Error(Marshal.GetLastWin32Error());
            if (pathLength >= path.Capacity) throw new IOException("FINAL_PATH_TOO_LONG");
            return (path.ToString(), volume, directory, length, modifiedAt);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void ValidateVolume(SafeFileHandle handle, string path)
    {
        // A drive alias to a subdirectory (SUBST) is not a volume root.
        if (!path.StartsWith(@"\\?\Volume{", StringComparison.Ordinal) || !path.EndsWith(@"}\", StringComparison.Ordinal)
            || path.IndexOf('}') != path.Length - 2 || GetDriveTypeW(path) is not (2 or 3))
            throw new IOException("LOCAL_VOLUME_ROOT_REQUIRED");
        var format = new StringBuilder(32);
        if (!GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out _, format, (uint)format.Capacity))
            throw Error(Marshal.GetLastWin32Error());
        if (format.ToString() is not ("NTFS" or "ReFS")) throw new IOException("UNSUPPORTED_FILESYSTEM");
    }

    internal static IReadOnlyList<string> Enumerate(SafeFileHandle directory, int scanLimit)
    {
        const int size = 65536;
        var buffer = Marshal.AllocHGlobal(size);
        var names = new List<string>();
        try
        {
            var restart = true;
            while (names.Count < scanLimit)
            {
                // FILE_FULL_DIR_INFO; offsets match the SDK structure on x86 and x64.
                if (!GetFileInformationByHandleEx(directory, restart ? 15 : 14, buffer, size))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 18) break; // ERROR_NO_MORE_FILES
                    throw Error(error);
                }
                restart = false;
                var offset = 0;
                while (true)
                {
                    if (offset < 0 || offset > size - 68) throw new IOException("INVALID_DIRECTORY_RECORD");
                    var entry = IntPtr.Add(buffer, offset);
                    var next = Marshal.ReadInt32(entry);
                    var bytes = Marshal.ReadInt32(entry, 60);
                    if (bytes < 0 || bytes % 2 != 0 || bytes > size - offset - 68)
                        throw new IOException("INVALID_DIRECTORY_RECORD");
                    var name = Marshal.PtrToStringUni(IntPtr.Add(entry, 68), bytes / 2)!;
                    if (name is not ("." or "..")) names.Add(name);
                    if (next == 0 || names.Count == scanLimit) break;
                    if (next < 68 || next > size - offset) throw new IOException("INVALID_DIRECTORY_RECORD");
                    offset += next;
                }
            }
            return names;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void RenameNoReplace(SafeFileHandle file, SafeFileHandle parent, string name)
    {
        var parentOffset = IntPtr.Size; // BOOLEAN/union padded to pointer alignment
        var lengthOffset = parentOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var bytes = Encoding.Unicode.GetBytes(name);
        // Include the terminator/padding in the buffer size, while FileNameLength excludes it.
        // This also satisfies the native structure minimum for a one-character name on x64.
        var size = nameOffset + bytes.Length + 2;
        var buffer = Marshal.AllocHGlobal(size);
        var pinned = false;
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            parent.DangerousAddRef(ref pinned);
            Marshal.WriteIntPtr(buffer, parentOffset, parent.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, bytes.Length);
            Marshal.Copy(bytes, 0, IntPtr.Add(buffer, nameOffset), bytes.Length);
            // FileRenameInformation = 10. ReplaceIfExists remains FALSE, single-component destination.
            var status = NtSetInformationFile(file, out _, buffer, (uint)size, 10);
            if (status < 0) throw Error(unchecked((int)RtlNtStatusToDosError(status)));
        }
        finally
        {
            if (pinned) parent.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void DeleteOnClose(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            var status = NtSetInformationFile(handle, out _, buffer, 1, 13); // FileDispositionInformation
            if (status < 0) throw Error(unchecked((int)RtlNtStatusToDosError(status)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out SafeFileHandle file, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock io, IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock io, IntPtr information, uint length, int informationClass);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, IntPtr information, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, StringBuilder? name, uint nameSize,
        out uint serial, out uint componentLength, out uint flags, StringBuilder format, uint formatSize);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
