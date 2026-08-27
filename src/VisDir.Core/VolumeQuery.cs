using System.ComponentModel;
using System.Runtime.InteropServices;
using VisDir.Core.Interop;

namespace VisDir.Core;

public static class VolumeQuery
{
    public static VolumeInfo Query(string path)
    {
        string root = PathUtils.NormalizeScanRoot(path);
        ulong total = 0, free = 0;
        uint cluster = 4096;
        string fsName = "";
        string displayName = root;
        ulong serial = 0;

        var hDir = NativeMethods.CreateFileW(
            PathUtils.Extend(root),
            NativeMethods.FILE_LIST_DIRECTORY,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        bool haveHandle = hDir != (IntPtr)(-1);
        if (haveHandle)
        {
            try
            {
                unsafe
                {
                    Span<char> nameBuf = stackalloc char[261];
                    Span<char> fsBuf = stackalloc char[65];
                    fixed (char* pName = nameBuf)
                    fixed (char* pFs = fsBuf)
                    {
                        if (NativeMethods.GetVolumeInformationByHandleW(
                                hDir, pName, 260, out uint serial32, out _, out _,
                                pFs, 64))
                        {
                            int len = IndexOfTerminator(pName, 260);
                            if (len > 0) displayName = new string(pName, 0, len);
                            len = IndexOfTerminator(pFs, 64);
                            if (len > 0) fsName = new string(pFs, 0, len);
                            serial = serial32;
                        }
                    }
                }
            }
            finally { NativeMethods.CloseHandle(hDir); }
        }

        // Drive-letter roots: get cluster geometry + capacity. UNC paths keep defaults.
        if (root.Length >= 2 && root[1] == ':')
        {
            string driveRoot = $"{root[0]}:\\";
            if (NativeMethods.GetDiskFreeSpaceW(driveRoot, out uint spc, out uint bps, out _, out _))
                cluster = spc * bps;
            if (NativeMethods.GetDiskFreeSpaceExW(driveRoot, out _, out ulong t, out ulong f))
            {
                total = t;
                free = f;
            }
        }
        else if (NativeMethods.GetDiskFreeSpaceExW(root, out _, out ulong t2, out ulong f2))
        {
            total = t2;
            free = f2;
        }

        return new VolumeInfo
        {
            RootPath = root,
            DisplayName = displayName,
            FileSystemName = fsName,
            VolumeSerialNumber = serial,
            BytesPerCluster = cluster,
            TotalBytes = total,
            FreeBytes = free,
        };
    }

    private static unsafe int IndexOfTerminator(char* p, int max)
    {
        for (int i = 0; i < max; i++)
            if (p[i] == '\0') return i;
        return max;
    }
}

public static class PathUtils
{
    /// <summary>Returns the path with a normalized trailing separator removed and consistent casing of drive letters.</summary>
    public static string NormalizeScanRoot(string path)
    {
        path = path.Trim();
        if (path.Length == 0) throw new ArgumentException("Empty path.", nameof(path));

        // A bare drive designator means the drive root, not that drive's process-relative
        // working directory. Resolve every other relative local path before adding the
        // extended Win32 prefix used by the compatible scanner.
        if (path.Length == 2 && path[1] == ':')
            path += "\\";
        else if (!Path.IsPathFullyQualified(path))
            path = Path.GetFullPath(path);

        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Length == 2 && path[1] == ':') path += "\\";
        return path;
    }

    /// <summary>Adds the Win32 long-path prefix (\\?\ or \\?\UNC\) when missing.</summary>
    public static string Extend(string path)
    {
        if (path.StartsWith(@"\\?\")) return path;
        if (path.StartsWith(@"\\") || path.StartsWith(@"//"))
            return @"\\?\UNC\" + path.TrimStart('\\', '/');
        return @"\\?\" + path;
    }

    public static string JoinDir(string dir, string name)
    {
        char last = dir[dir.Length - 1];
        return last == '\\' || last == '/' ? string.Concat(dir, name) : string.Concat(dir, "\\", name);
    }
}
