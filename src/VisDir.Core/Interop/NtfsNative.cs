using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace VisDir.Core.Interop;

/// <summary>NTFS-specific volume and raw-read support for the MFT fast path.</summary>
[SuppressMessage("ReSharper", "IdentifierTypo")]
public static unsafe class NtfsNative
{
    public const uint FsctlGetNtfsVolumeData = 0x00090064; // CTL_CODE(0x09, 25, BUFFERED, ANY)
    public const uint IoctlDiskGetDriveGeometry = 0x00070000;

    public const uint GenericRead = 0x80000000;
    public const uint ShareReadWriteDelete =
        NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE;
    public const uint OpenExisting = NativeMethods.OPEN_EXISTING;
    public const uint FileFlagOverlapped = 0x40000000;
    public const int ErrorIoPending = 997;
    public const uint Infinite = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    public struct OVERLAPPED
    {
        public UIntPtr Internal;
        public UIntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    // FILE_*_INFORMATION attribute flags (record-level).
    public const ushort MftRecordInUse = 0x0001;
    public const ushort MftRecordIsDirectory = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct NtfsVolumeDataBuffer
    {
        public ulong VolumeSerialNumber;
        public ulong NumberSectors;
        public ulong TotalClusters;
        public ulong FreeClusters;
        public ulong TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public ulong MftValidDataLength;
        public ulong MftStartLcn;
        public ulong Mft2StartLcn;
        public ulong StartOfUserArea;
        public ulong MaximumNumberOfVersions;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    public static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        out NtfsVolumeDataBuffer lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        IntPtr hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFilePointerEx(
        IntPtr hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);

    // Overlapped double-buffer support (MFT Tier-1 only): the existing ReadFile above
    // doubles as the async entry point by passing an OVERLAPPED* as lpOverlapped.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateEventW(
        IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(
        IntPtr hFile,
        OVERLAPPED* lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool bWait);
}
