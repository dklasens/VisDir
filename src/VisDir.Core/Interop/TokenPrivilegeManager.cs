using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace VisDir.Core.Interop;

/// <summary>
/// Manages Windows process token privileges such as SeBackupPrivilege and SeRestorePrivilege
/// to enable reading restricted files and volumes with backup semantics.
/// </summary>
[SuppressMessage("ReSharper", "IdentifierTypo")]
public static class TokenPrivilegeManager
{
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    public const string SE_BACKUP_NAME = "SeBackupPrivilege";
    public const string SE_RESTORE_NAME = "SeRestorePrivilege";
    public const string SE_MANAGE_VOLUME_NAME = "SeManageVolumePrivilege";

    private const int ERROR_SUCCESS = 0;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;

        public LUID_AND_ATTRIBUTES Privilege
        {
            readonly get => Privileges;
            set => Privileges = value;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(
        IntPtr ProcessHandle,
        uint DesiredAccess,
        out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LookupPrivilegeValueW(
        string? lpSystemName,
        string lpName,
        out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Attempts to enable a specific privilege on the current process token.
    /// Returns true if the privilege was successfully enabled.
    /// Returns false if not found or if the token does not possess the privilege (e.g. non-elevated).
    /// Guaranteed to never throw.
    /// </summary>
    public static bool TryEnablePrivilege(string privilegeName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(privilegeName))
        {
            return false;
        }

        IntPtr tokenHandle = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tokenHandle))
            {
                return false;
            }

            if (!LookupPrivilegeValueW(null, privilegeName, out LUID luid))
            {
                return false;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED,
                },
            };

            if (!AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                return false;
            }

            return Marshal.GetLastWin32Error() == ERROR_SUCCESS;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero && tokenHandle != (IntPtr)(-1))
            {
                CloseHandle(tokenHandle);
            }
        }
    }

    /// <summary>
    /// Attempts to enable backup privileges (<c>SeBackupPrivilege</c>, <c>SeRestorePrivilege</c>,
    /// and <c>SeManageVolumePrivilege</c>) for the current process token.
    /// Returns true if <c>SeBackupPrivilege</c> was successfully enabled.
    /// Guaranteed to never throw.
    /// </summary>
    public static bool TryEnableBackupPrivileges()
    {
        try
        {
            bool backup = TryEnablePrivilege(SE_BACKUP_NAME);
            TryEnablePrivilege(SE_RESTORE_NAME);
            TryEnablePrivilege(SE_MANAGE_VOLUME_NAME);
            return backup;
        }
        catch
        {
            return false;
        }
    }
}
