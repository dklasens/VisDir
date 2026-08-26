using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace VisDir.Core.Interop;

[SuppressMessage("ReSharper", "IdentifierTypo")]
public static unsafe class NativeMethods
{
    public const uint FILE_LIST_DIRECTORY = 0x00000001;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_MORE_DATA = 234;

    // FILE_ID_EXTD_DIR_INFO fixed-field offsets (see FILE_ID_EXTD_DIR_INFO docs).
    public const int Extd_NextEntryOffset = 0;
    public const int Extd_EndOfFile = 40;
    public const int Extd_AllocationSize = 48;
    public const int Extd_Attributes = 56;
    public const int Extd_NameLengthBytes = 60;
    public const int Extd_ReparsePointTag = 68;
    public const int Extd_FileId = 72;          // FILE_ID_128, low 8 bytes used
    public const int Extd_FixedSize = 88;       // name chars follow at +88

    // FILE_FULL_DIR_INFO fixed-field offsets (fallback for FS that lack extd class).
    public const int Full_FixedSize = 68;
    public const int Full_EndOfFile = 40;
    public const int Full_AllocationSize = 48;
    public const int Full_Attributes = 56;
    public const int Full_NameLengthBytes = 60;

    public const int FileIdExtdDirectoryInfoClass = 19;
    public const int FileIdExtdDirectoryRestartInfoClass = 20;
    public const int FileFullDirectoryInfoClass = 14;
    public const int FileFullDirectoryRestartInfoClass = 15;

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateFileW", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandleEx(
        IntPtr hFile,
        int fileInformationClass,
        IntPtr lpBuffer,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetVolumeInformationByHandleW", CharSet = CharSet.Unicode)]
    public static extern bool GetVolumeInformationByHandleW(
        IntPtr hFile,
        char* lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        char* lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode)]
    public static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode)]
    public static extern bool GetDiskFreeSpaceW(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);
}
