using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Portalkeeper.Services;

// Classifies why a hard link could not be created so runtime construction can
// give precise diagnostics instead of converting every failure into a copy.
public enum HardLinkFailure
{
    None,
    CrossDevice,          // source and destination are on different volumes
    PermissionDenied,     // the caller cannot create the link
    DestinationExists,    // the destination path already contains something
    SourceNotFound,       // the source file does not exist
    InvalidPath,          // malformed path / used as a path where a file is expected
    Unexpected            // any other filesystem error
}

// Safe hard-link creation and identity verification for managed runtime
// construction. Uses native OS calls on both Windows and Linux rather than
// shelling out to mklink/ln. A hard-linked baseline file shares its file
// identity (volume + file id) with the source file; that identity check is
// the strongest portable verification we have, and it does not mutate either
// file.
public sealed class HardLinkService
{
    // True on the platforms where file identity (volume + inode/file-id) can be
    // verified directly. Where this is false the validator falls back to
    // existence-and-containment checks rather than failing construction.
    public bool CanVerifyFileIdentity =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>
    /// Creates a hard link at <paramref name="destination"/> pointing at
    /// <paramref name="source"/>. Returns false (never throws for expected
    /// filesystem failures) and classifies the reason via
    /// <paramref name="failure"/>.
    /// </summary>
    public bool TryCreateHardLink(string source, string destination, out HardLinkFailure failure)
    {
        failure = HardLinkFailure.None;

        if (string.IsNullOrWhiteSpace(destination))
        {
            failure = HardLinkFailure.InvalidPath;
            return false;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            failure = HardLinkFailure.InvalidPath;
            return false;
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            failure = HardLinkFailure.DestinationExists;
            return false;
        }

        if (!File.Exists(source))
        {
            failure = HardLinkFailure.SourceNotFound;
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsNative.TryCreateHardLink(source, destination, out failure);

            if (OperatingSystem.IsLinux())
                return LinuxNative.TryCreateHardLink(source, destination, out failure);

            failure = HardLinkFailure.Unexpected;
            return false;
        }
        catch (Exception)
        {
            failure = HardLinkFailure.Unexpected;
            return false;
        }
    }

    /// <summary>
    /// True when both paths resolve into the same filesystem/volume. Returns
    /// null when the platform cannot determine it (never a hard failure; the
    /// actual link attempt remains authoritative).
    /// </summary>
    public bool? SameFilesystem(string left, string right)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var leftId = WindowsNative.TryGetFileId(left);
                var rightId = WindowsNative.TryGetFileId(right);
                if (leftId is not null && rightId is not null)
                    return leftId.Value.Volume == rightId.Value.Volume;
                return null;
            }

            if (OperatingSystem.IsLinux())
            {
                var leftId = LinuxNative.TryGetFileId(left);
                var rightId = LinuxNative.TryGetFileId(right);
                if (leftId is not null && rightId is not null)
                    return leftId.Value.Device == rightId.Value.Device;
                return null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when two files share the same file identity, i.e. they are hard
    /// links to the same underlying file on the same volume.
    /// </summary>
    public bool AreSameFile(string left, string right)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var leftId = WindowsNative.TryGetFileId(left);
                var rightId = WindowsNative.TryGetFileId(right);
                return leftId is not null && rightId is not null &&
                       leftId.Value.Volume == rightId.Value.Volume &&
                       leftId.Value.Index == rightId.Value.Index;
            }

            if (OperatingSystem.IsLinux())
            {
                var leftId = LinuxNative.TryGetFileId(left);
                var rightId = LinuxNative.TryGetFileId(right);
                return leftId is not null && rightId is not null &&
                       leftId.Value.Device == rightId.Value.Device &&
                       leftId.Value.Inode == rightId.Value.Inode;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public string DescribeFailure(HardLinkFailure failure) => failure switch
    {
        HardLinkFailure.CrossDevice =>
            "The source client and the runtime location are on different filesystems; " +
            "hard linking is not possible across volumes and Portalkeeper does not " +
            "silently copy gigabytes. Choose a runtime location on the same volume " +
            "as the source client.",
        HardLinkFailure.PermissionDenied =>
            "Portalkeeper was denied permission to create a hard link in the runtime location.",
        HardLinkFailure.DestinationExists =>
            "The runtime destination already contains a file that Portalkeeper does not own.",
        HardLinkFailure.SourceNotFound =>
            "A baseline source file expected for the runtime does not exist in the source client.",
        HardLinkFailure.InvalidPath =>
            "A hard link could not be created because a path is invalid.",
        HardLinkFailure.Unexpected =>
            "A hard link could not be created due to an unexpected filesystem error.",
        _ => string.Empty
    };

    private static class WindowsNative
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(
            string fileName,
            string existingFileName,
            IntPtr lpSecurityAttributes);

        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 1;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
            out ByHandleFileInformationByHandle info);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformationByHandle
        {
            public uint DwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME FtCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME FtLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME FtLastWriteTime;
            public uint DwVolumeSerialNumber;
            public uint NFileSizeHigh;
            public uint NFileSizeLow;
            public uint NNumberOfLinks;
            public uint NFileIndexHigh;
            public uint NFileIndexLow;
        }

        public static bool TryCreateHardLink(string source, string destination, out HardLinkFailure failure)
        {
            failure = HardLinkFailure.None;
            if (!CreateHardLinkW(destination, source, IntPtr.Zero))
            {
                failure = Classify(Marshal.GetLastWin32Error());
                return false;
            }
            return true;
        }

        private static HardLinkFailure Classify(int error) => error switch
        {
            0x11 => HardLinkFailure.CrossDevice,          // ERROR_NOT_SAME_DEVICE
            5 => HardLinkFailure.PermissionDenied,        // ERROR_ACCESS_DENIED
            183 => HardLinkFailure.DestinationExists,     // ERROR_ALREADY_EXISTS
            3 or 2 => HardLinkFailure.SourceNotFound,     // ERROR_PATH_NOT_FOUND / FILE_NOT_FOUND
            87 => HardLinkFailure.InvalidPath,            // ERROR_INVALID_PARAMETER
            123 => HardLinkFailure.InvalidPath,           // ERROR_INVALID_NAME
            _ => HardLinkFailure.Unexpected
        };

        public static (bool Ok, uint Volume, ulong Index)? TryGetFileId(string path)
        {
            using var handle = CreateFileW(
                path,
                GenericRead,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                BackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
                return null;

            return (true, info.DwVolumeSerialNumber, ((ulong)info.NFileIndexHigh << 32) | info.NFileIndexLow);
        }
    }

    private static class LinuxNative
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int link(string oldpath, string newpath);

        [DllImport("libc", SetLastError = true)]
        private static extern int stat(string path, out NativeStat buffer);

        // glibc x86_64 struct stat (144 bytes); we only read st_dev and st_ino
        // but must marshal the full buffer so stat() does not overflow it.
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeStat
        {
            public ulong StDev;      // offset 0
            public ulong StIno;      // offset 8
            public ulong StNlink;
            public uint StMode;
            public uint StUid;
            public uint StGid;
            public int Pad0;
            public ulong StRdev;
            public long StSize;
            public long StBlksize;
            public long StBlocks;
            public long AtimSec;
            public long AtimNsec;
            public long MtimSec;
            public long MtimNsec;
            public long CtimSec;
            public long CtimNsec;
            public long Reserved0;
            public long Reserved1;
            public long Reserved2;
        }

        public static bool TryCreateHardLink(string source, string destination, out HardLinkFailure failure)
        {
            failure = HardLinkFailure.None;
            if (link(source, destination) != 0)
            {
                failure = Classify(Marshal.GetLastWin32Error());
                return false;
            }
            return true;
        }

        private static HardLinkFailure Classify(int error) => error switch
        {
            18 => HardLinkFailure.CrossDevice,        // EXDEV
            1 or 13 => HardLinkFailure.PermissionDenied, // EPERM / EACCES
            17 => HardLinkFailure.DestinationExists,  // EEXIST
            2 => HardLinkFailure.SourceNotFound,      // ENOENT
            20 or 22 => HardLinkFailure.InvalidPath,  // ENOTDIR / EINVAL
            _ => HardLinkFailure.Unexpected
        };

        public static (bool Ok, ulong Device, ulong Inode)? TryGetFileId(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return null;

            if (stat(path, out var buffer) != 0)
                return null;

            return (true, buffer.StDev, buffer.StIno);
        }
    }
}