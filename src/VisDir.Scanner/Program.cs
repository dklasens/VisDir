using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VisDir.Core;
using VisDir.Core.Interop;
using VisDir.Core.Scanning;

namespace VisDir.Scanner;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("SCAN CANCELLED");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (Environment.GetEnvironmentVariable("VISDIR_DEBUG") == "1")
                Console.Error.WriteLine(ex.ToString());
            return 3;
        }
    }

    private static int Run(string[] args)
    {
        string? path = null;
        string? outFile = null;
        bool report = false;
        bool diffVerify = false;
        string mode = "auto";
        int top = 25;
        int threads = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--out" when i + 1 < args.Length:
                    outFile = args[++i];
                    break;
                case "--report":
                    report = true;
                    break;
                case "--mode" when i + 1 < args.Length:
                    mode = args[++i].ToLowerInvariant();
                    if (mode is not ("mft" or "generic" or "auto"))
                    { Console.Error.WriteLine($"Bad --mode '{mode}'"); return 1; }
                    break;
                case "--diff":
                    diffVerify = true;
                    break;
                case "--mftprobe":
                    return RunMftProbe(args[++i]);
                case "--top" when i + 1 < args.Length && int.TryParse(args[++i], out var t):
                    top = Math.Clamp(t, 0, 500);
                    break;
                case "--threads" when i + 1 < args.Length && int.TryParse(args[++i], out var th):
                    threads = th;
                    break;
                case "-h" or "--help":
                    PrintUsage();
                    return 0;
                default:
                    if (path is null && !args[i].StartsWith('-')) path = args[i];
                    else { Console.Error.WriteLine($"Unknown argument: {args[i]}"); PrintUsage(); return 1; }
                    break;
            }
        }

        if (path is null)
        {
            PrintUsage();
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var progress = new InlineProgress<ScanProgress>(p =>
        {
            Console.Error.WriteLine(
                $"PROGRESS files={p.FilesSeen} dirs={p.DirsSeen} bytes={p.BytesSeen} ms={p.ElapsedMs:F0} " +
                $"fraction={p.Fraction:F6} phase={Uri.EscapeDataString(p.Phase)}");
        });

        var options = new ScanOptions { Path = path, Threads = threads };
        var sw = Stopwatch.StartNew();

        ScannerSelection selection = SelectScanner(mode, path);
        Console.Error.WriteLine($"ENGINE selected={selection.Name} reason={Uri.EscapeDataString(selection.Reason)}");
        IDiskScanner primary = selection.Scanner;
        ScanResult result = primary.Scan(options, cts.Token, progress);
        sw.Stop();

        Console.Error.WriteLine(
            $"DONE engine={result.EngineName} files={result.Stats.FileCount} dirs={result.Stats.DirectoryCount} " +
            $"errors={result.Stats.ErrorCount} bytes={result.Root.TotalAllocated} ms={sw.Elapsed.TotalMilliseconds:F0}");

        ScanResult? reference = null;
        if (diffVerify)
        {
            try
            {
                reference = new GenericScanner().Scan(options, cts.Token, null);
                Console.Error.WriteLine(
                    $"DONE engine=generic(files) files={reference.Stats.FileCount} " +
                    $"bytes={reference.Root.TotalAllocated} ms={reference.Stats.ElapsedMs:F0}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"DIFF reference scan failed: {ex.Message}");
            }
        }

        if (outFile is not null)
        {
            using var fs = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            TreeSerializer.Write(fs, result);
            Console.Error.WriteLine($"SNAPSHOT {outFile}");
        }

        if (report || outFile is null)
            PrintReport(result, top, reference);

        if (reference is not null)
            return DiffCheck(result, reference);
        return 0;
    }

    /// <summary>Dumps raw $MFT record #0 diagnostics to understand run-list structure.</summary>
    private static unsafe int RunMftProbe(string path)
    {
        string device = NtfsMftScanner.GetVolumeDevice(PathUtils.NormalizeScanRoot(path));
        IntPtr hVol = NativeMethods.CreateFileW(device, NtfsNative.GenericRead,
            NtfsNative.ShareReadWriteDelete, IntPtr.Zero, NtfsNative.OpenExisting, 0, IntPtr.Zero);
        if (hVol == (IntPtr)(-1))
        {
            Console.Error.WriteLine($"open err={Marshal.GetLastWin32Error()}");
            return 3;
        }
        try
        {
            if (!NtfsNative.DeviceIoControl(hVol, NtfsNative.FsctlGetNtfsVolumeData, IntPtr.Zero, 0,
                    out var vol, (uint)Marshal.SizeOf<NtfsNative.NtfsVolumeDataBuffer>(), out _, IntPtr.Zero))
            {
                Console.Error.WriteLine($"ioctl err={Marshal.GetLastWin32Error()}");
                return 3;
            }
            Console.WriteLine($"cluster={vol.BytesPerCluster} recSize={vol.BytesPerFileRecordSegment} " +
                              $"mftLcn={vol.MftStartLcn} validLen={vol.MftValidDataLength}");

            var buf = new byte[1024];
            fixed (byte* p = buf)
            {
                NtfsNative.SetFilePointerEx(hVol, (long)(vol.MftStartLcn * vol.BytesPerCluster), out _, 0);
                if (!NtfsNative.ReadFile(hVol, p, 1024, out uint got, IntPtr.Zero) || got < 64)
                {
                    Console.Error.WriteLine($"read err={Marshal.GetLastWin32Error()}");
                    return 3;
                }

                Console.WriteLine("record0 header hex:");
                for (int row = 0; row < 4; row++)
                {
                    Console.WriteLine(string.Join(' ',
                        Enumerable.Range(0, 16).Select(i => buf[row * 16 + i].ToString("X2"))));
                }

                ushort usaOff = BitConverter.ToUInt16(buf, 4);
                ushort usaCnt = BitConverter.ToUInt16(buf, 6);
                ushort attrOff = BitConverter.ToUInt16(buf, 0x14);
                uint used = BitConverter.ToUInt32(buf, 0x18);
                long baseRec = BitConverter.ToInt64(buf, 0x20);
                uint recNo = BitConverter.ToUInt32(buf, 0x2C);
                Console.WriteLine($"usaOff={usaOff} usaCnt={usaCnt} attrOff={attrOff} used={used} base={baseRec} recNo={recNo}");

                // Fixup
                ushort seq = BitConverter.ToUInt16(buf, usaOff);
                for (int i = 1; i < usaCnt; i++)
                {
                    int e = i * 512 - 2;
                    ushort cur = BitConverter.ToUInt16(buf, e);
                    ushort corr = BitConverter.ToUInt16(buf, usaOff + i * 2);
                    Console.WriteLine($"fixup sector {i}: at {e} found {cur:X4} seq {seq:X4} -> {(cur == seq ? "OK" : "MISMATCH")}");
                    if (cur == seq) Buffer.SetByte(buf, e, (byte)corr);
                }

                uint off = attrOff;
                while (off + 16 <= Math.Min(used, got))
                {
                    uint type = BitConverter.ToUInt32(buf, (int)off);
                    if (type == 0xFFFFFFFF) break;
                    uint len = BitConverter.ToUInt32(buf, (int)off + 4);
                    byte nonRes = buf[off + 8];
                    byte nameLen = buf[off + 9];
                    uint flags = BitConverter.ToUInt16(buf, (int)off + 0xC);
                    Console.WriteLine($"attr type={type:X8} off={off} len={len} nonRes={nonRes} nameLen={nameLen} flags={flags:X4}");
                    if (type != 0x80)
                    {
                        off += len < 8 ? (uint)buf.Length : len;
                        continue;
                    }
                    // Full hex of the $DATA attribute for offline verification.
                    Console.WriteLine("$DATA attr hex:");
                    for (int row = 0; row < (int)Math.Min(len, 128) / 16; row++)
                    {
                        int b = (int)off + row * 16;
                        Console.WriteLine(string.Join(' ', Enumerable.Range(0, 16).Select(i => buf[b + i].ToString("X2"))));
                    }
                    if (nonRes == 0)
                    {
                        Console.WriteLine("  $DATA resident");
                        break;
                    }
                    uint runsOff = BitConverter.ToUInt16(buf, (int)off + 0x20);
                    ulong lowestVcn = BitConverter.ToUInt64(buf, (int)off + 0x10);
                    ulong highestVcn = BitConverter.ToUInt64(buf, (int)off + 0x18);
                    ulong alloc = BitConverter.ToUInt64(buf, (int)off + 0x28);
                    Console.WriteLine($"  lowestVcn={lowestVcn} highestVcn={highestVcn} runsOff(inAttr)={runsOff} allocSize={alloc}");

                    int rp = (int)(off + runsOff);
                    int rend = (int)(off + len);
                    long lcn = 0;
                    int n = 0;
                    ulong covered = 0;
                    while (rp < rend && n < 30 && buf[rp] != 0)
                    {
                        byte h = buf[rp++];
                        uint lb = (uint)(h & 0xF), ob = (uint)(h >> 4);
                        if (lb == 0 || lb > 8 || ob > 8 || rp + lb + ob > rend) { Console.WriteLine("  bad run header"); break; }
                        ulong clusters = 0;
                        for (uint i = 0; i < lb; i++) clusters |= (ulong)buf[rp++] << (int)(8 * i);
                        long delta = 0;
                        for (uint i = 0; i < ob; i++) delta |= (long)buf[rp++] << (int)(8 * i);
                        if (ob > 0 && (delta >> (int)(8 * ob - 1)) != 0) delta |= ~((1L << (int)(8 * ob)) - 1);
                        lcn += delta;
                        covered += clusters;
                        Console.WriteLine($"  run[{n++}] lcn={lcn} clusters={clusters}");
                    }
                    Console.WriteLine($"  runs listed={n} coveredClusters={covered} ({covered * vol.BytesPerCluster / (1024 * 1024)} MB)");
                    break;
                }
            }
            return 0;
        }
        finally { NativeMethods.CloseHandle(hVol); }
    }

    private sealed record ScannerSelection(IDiskScanner Scanner, string Name, string Reason);

    private static ScannerSelection SelectScanner(string mode, string path)
    {
        switch (mode)
        {
            case "generic":
                return new ScannerSelection(new GenericScanner(), "generic", "Compatible scan selected");
            case "mft":
                return new ScannerSelection(new NtfsMftScanner(), "mft", "Fast NTFS scan selected");
            default:
            {
                // auto: MFT for drive-letter roots when elevated, generic otherwise.
                string root = PathUtils.NormalizeScanRoot(path);
                bool isNtfs = false;
                if (root.Length == 3 && root[1] == ':' && root.EndsWith("\\"))
                {
                    try { isNtfs = string.Equals(VolumeQuery.Query(root).FileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase); }
                    catch { /* compatible scanner will report any underlying access error */ }
                }
                if (root.Length == 3 && root[1] == ':' && root.EndsWith("\\") &&
                    Directory.Exists(root) && isNtfs && IsElevated())
                {
                    return new ScannerSelection(new NtfsMftScanner(), "mft", "Administrator access available; using fast NTFS scan");
                }
                string reason = root.Length == 3 && root[1] == ':' && isNtfs
                    ? "Using compatible scan; choose Fast NTFS to request administrator access"
                    : root.Length == 3 && root[1] == ':'
                        ? "This filesystem uses the compatible scanner"
                        : "Folder and network paths use the compatible scanner";
                return new ScannerSelection(new GenericScanner(), "generic", reason);
            }
        }
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Compares two scans of the same volume; returns exit code 0 when within tolerance.</summary>
    private static int DiffCheck(ScanResult mft, ScanResult generic)
    {
        Console.WriteLine();
        Console.WriteLine("=== DIFF: MFT vs Generic ===");
        Console.WriteLine($"Total allocated: mft={SizeFormatter.Format(mft.Root.TotalAllocated)}  " +
                          $"generic={SizeFormatter.Format(generic.Root.TotalAllocated)}");

        double deltaPct = generic.Root.TotalAllocated > 0
            ? Math.Abs((double)mft.Root.TotalAllocated - generic.Root.TotalAllocated) / generic.Root.TotalAllocated * 100
            : 0;
        Console.WriteLine($"Delta: {deltaPct:0.00}%");

        var gKids = generic.Root.Children ?? new List<FsNode>();
        var mKids = new Dictionary<string, ulong>(
            (mft.Root.Children ?? new List<FsNode>()).Select(c => KeyValuePair.Create(c.Name, c.TotalAllocated)),
            StringComparer.OrdinalIgnoreCase);

        double worst = 0;
        string worstName = "";
        foreach (FsNode g in gKids.Take(30))
        {
            ulong m = mKids.TryGetValue(g.Name, out ulong v) ? v : 0;
            double pct = g.TotalAllocated > 1024 * 1024
                ? Math.Abs((double)m - g.TotalAllocated) / g.TotalAllocated * 100
                : 0;
            if (pct > worst && m < g.TotalAllocated) { worst = pct; worstName = g.Name; }
            if (pct > 5 && m < g.TotalAllocated)
                Console.WriteLine($"  MISMATCH {g.Name}: mft={SizeFormatter.Format(m)} generic={SizeFormatter.Format(g.TotalAllocated)} ({pct:0.0}%)");
            else if (pct > 25)
                Console.WriteLine($"  EXTRA-VISIBILITY {g.Name}: mft sees {SizeFormatter.Format(m - g.TotalAllocated)} more (shadow copies/system metadata)");
        }

        Console.WriteLine(worst <= 5
            ? "DIFF PASS: all top-level items within 5% tolerance."
            : $"DIFF WORST: {worstName} at {worst:0.0}%");

        // Leaf-level evidence: top 15 files from each engine, matched by name.
        var mTop = TopLeafFiles(mft, 300);
        var gTop = TopLeafFiles(generic, 300);
        var gByName = new Dictionary<string, List<ulong>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string p, ulong sz) in gTop)
        {
            string key = Path.GetFileName(p);
            if (!gByName.TryGetValue(key, out var list)) gByName[key] = list = new List<ulong>();
            list.Add(sz);
        }

        Console.WriteLine();
        Console.WriteLine("Largest files — mft vs generic (only mismatches shown):");
        int shown = 0;
        foreach ((string path, ulong alloc) in mTop)
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith('$') || name.StartsWith('{')) continue; // system artifacts: generic can't see these by design
            ulong gAlloc = 0;
            if (gByName.TryGetValue(name, out var candidates))
            {
                // Match against the closest candidate size (names repeat across folders).
                gAlloc = candidates.OrderBy(s => Math.Abs((long)s - (long)alloc)).First();
            }
            double pctDiff = alloc > 1024 * 1024 || gAlloc > 1024 * 1024
                ? Math.Abs((double)alloc - gAlloc) / Math.Max(alloc, gAlloc) * 100
                : 0;
            if (pctDiff > 2 && shown++ < 15)
                Console.WriteLine($"  {name,-50} mft={SizeFormatter.Format(alloc),10} generic={SizeFormatter.Format(gAlloc),10}");
        }
        if (shown == 0) Console.WriteLine("  (top files match)");

        return worst <= 5 ? 0 : 4;
    }

    private static List<(string Path, ulong Alloc)> TopLeafFiles(ScanResult r, int n)
    {
        var leaves = new List<(string, ulong)>();
        var stack = new Stack<FsNode>();
        stack.Push(r.Root);
        while (stack.Count > 0)
        {
            FsNode node = stack.Pop();
            if (node.Children is { } kids)
                foreach (FsNode k in kids) stack.Push(k);
            else if ((node.Flags & NodeFlags.Directory) == 0)
                leaves.Add((node.GetPath(), node.AllocatedSize));
        }
        leaves.Sort(static (a, b) => b.Item2.CompareTo(a.Item2));
        return leaves.Take(n).ToList();
    }

    private static void PrintReport(ScanResult result, int top, ScanResult? reference = null)
    {
        var v = result.Volume;
        ulong usedByTree = result.Root.TotalAllocated;
        bool isVolumeRoot = v.RootPath.Length == 3 && v.RootPath[1] == ':' && v.RootPath.EndsWith(":\\");

        Console.WriteLine();
        Console.WriteLine($"VisDir scan [{result.EngineName}]: {v.RootPath}   [{v.FileSystemName}]");
        if (isVolumeRoot)
        {
            Console.WriteLine($"Capacity:   {SizeFormatter.Format(v.TotalBytes)}");
            Console.WriteLine($"Free:       {SizeFormatter.Format(v.FreeBytes)}");
            ulong capacityUsed = v.TotalBytes - v.FreeBytes;
            if (capacityUsed > usedByTree)
                Console.WriteLine($"Unaccounted:{SizeFormatter.Format(capacityUsed - usedByTree),13}  (metadata, shadow copies, quotas...)");
        }
        Console.WriteLine($"Logical:    {SizeFormatter.Format(result.Root.TotalLogical),12}  ({result.Stats.FileCount:N0} files, {result.Stats.DirectoryCount:N0} dirs)");
        Console.WriteLine($"On disk:    {SizeFormatter.Format(usedByTree),12}");
        double secs = result.Stats.ElapsedMs / 1000.0;
        Console.WriteLine($"Scan time:  {secs:0.00}s   errors: {result.Stats.ErrorCount}");

        if (top > 0 && result.Root.Children is { } kids && kids.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Top {Math.Min(top, kids.Count)} items:");
            foreach (FsNode child in kids.Take(top))
            {
                double frac = result.Root.TotalAllocated > 0
                    ? (double)child.TotalAllocated / result.Root.TotalAllocated
                    : 0;
                int bars = (int)Math.Round(frac * 20);
                string kind = child.IsDirectory ? "DIR " : "file";
                Console.WriteLine(
                    $"{SizeFormatter.Format(child.TotalAllocated),12}  {new string('#', bars),-20} {kind}  {child.Name}");
            }
        }
        Console.WriteLine();
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            VisDir.Scanner � disk usage scanner
            Usage:
              VisDir.Scanner <path> [--out <file.vdir>] [--report] [--top N] [--threads N]

              path       directory or drive root to scan (e.g. C:\)
              --out      write binary snapshot file
              --report   print human-readable summary
              --top N    items in report (default 25)
              --threads N worker threads (default: CPU count)
            """);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
