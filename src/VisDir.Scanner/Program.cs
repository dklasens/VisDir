using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VisDir.Core;
using VisDir.Core.Interop;
using VisDir.Core.Scanning;

namespace VisDir.Scanner;

[SupportedOSPlatform("windows")]
public static class ScannerCli
{
    /// <summary>Runs the scanner command line inside the WPF application's isolated worker mode.</summary>
    public static int Execute(string[] args)
    {
        try
        {
            TokenPrivilegeManager.TryEnableBackupPrivileges();
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
        TokenPrivilegeManager.TryEnableBackupPrivileges();
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
                case "--out":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("Missing value for --out."); return 1; }
                    outFile = args[++i];
                    break;
                case "--report":
                    report = true;
                    break;
                case "--mode":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("Missing value for --mode."); return 1; }
                    mode = args[++i].ToLowerInvariant();
                    if (mode is not ("mft" or "generic" or "auto"))
                    { Console.Error.WriteLine($"Bad --mode '{mode}'"); return 1; }
                    break;
                case "--diff":
                    diffVerify = true;
                    break;
                case "--mftprobe":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("Missing path for --mftprobe."); return 1; }
                    return RunMftProbe(args[++i]);
                case "--top":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("Missing value for --top."); return 1; }
                    if (!int.TryParse(args[++i], out int t))
                    { Console.Error.WriteLine($"Bad --top value '{args[i]}'"); return 1; }
                    top = Math.Clamp(t, 0, 500);
                    break;
                case "--threads":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("Missing value for --threads."); return 1; }
                    if (!int.TryParse(args[++i], out int th))
                    { Console.Error.WriteLine($"Bad --threads value '{args[i]}'"); return 1; }
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

        if (outFile is not null)
        {
            string? snapshotError = ValidateSnapshotPath(outFile);
            if (snapshotError is not null) { Console.Error.WriteLine(snapshotError); return 1; }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Worker emits PROGRESS over stderr; the parent coalesces at 10Hz and DONE always flushes.
        // Throttle here to >=500ms between emits so large scans don't flood the pipe; line format unchanged.
        long lastProgressMs = Environment.TickCount64 - 500;
        var progress = new InlineProgress<ScanProgress>(p =>
        {
            long now = Environment.TickCount64;
            if (now - Volatile.Read(ref lastProgressMs) < 500) return;
            Volatile.Write(ref lastProgressMs, now);
            Console.Error.WriteLine(FormattableString.Invariant(
                $"PROGRESS files={p.FilesSeen} dirs={p.DirsSeen} bytes={p.BytesSeen} ms={p.ElapsedMs:F0} fraction={p.Fraction:F6} phase={Uri.EscapeDataString(p.Phase)}"));
        });

        var options = new ScanOptions { Path = path, Threads = threads };
        var sw = Stopwatch.StartNew();

        var coordinator = new ScanCoordinator();
        ScanResult result = coordinator.Run(options, mode, cts.Token, progress,
            selection => Console.Error.WriteLine($"ENGINE selected={selection.Name} reason={Uri.EscapeDataString(selection.Reason)}"));
        sw.Stop();

        Console.Error.WriteLine(
            $"DONE engine={result.EngineName} files={result.Stats.FileCount} dirs={result.Stats.DirectoryCount} " +
            $"errors={result.Stats.ErrorCount} bytes={result.Root.TotalAllocated} ms={sw.Elapsed.TotalMilliseconds:F0}");

        ScanResult? reference = null;
        if (diffVerify && result.EngineName == "mft" && result.FallbackReason is null)
        {
            try
            {
                reference = coordinator.Run(options, "generic", cts.Token, null);
                Console.Error.WriteLine(
                    $"DONE engine=generic(files) files={reference.Stats.FileCount} " +
                    $"bytes={reference.Root.TotalAllocated} ms={reference.Stats.ElapsedMs:F0}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"DIFF reference scan failed: {ex.Message}");
            }
        }

        if (outFile is not null)
        {
            // Temp + rename in the destination directory: a crash never leaves a half-written snapshot.
            string full = Path.GetFullPath(outFile);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string temp = full + $".partial-{Guid.NewGuid():N}";
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 18))
                    TreeSerializer.Write(fs, result);
                File.Move(temp, full, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
            Console.Error.WriteLine($"SNAPSHOT {full}");
        }

        if (report || outFile is null)
            PrintReport(result, top, reference);

        if (diffVerify)
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

    private static int DiffCheck(ScanResult primary, ScanResult? reference)
    {
        ComparisonResult comparison = ScanComparison.Compare(primary, reference);
        Console.WriteLine();
        Console.WriteLine($"DIFF {comparison.Status.ToString().ToUpperInvariant()}: MFT vs Compatible");
        Console.WriteLine($"Comparable allocation: MFT={comparison.PrimaryBytes:N0}, Compatible={comparison.ReferenceBytes:N0} bytes");
        if (comparison.ExcludedBytes > 0)
            Console.WriteLine($"Explicitly excluded MFT metadata/stream allocation: {comparison.ExcludedBytes:N0} bytes");
        foreach (string detail in comparison.Details) Console.WriteLine("  " + detail);
        if (comparison.MismatchCount > comparison.Details.Count)
            Console.WriteLine($"  {comparison.MismatchCount - comparison.Details.Count:N0} additional mismatches");
        return comparison.Status switch
        {
            ComparisonStatus.Passed => 0,
            ComparisonStatus.Failed => 4,
            _ => 5,
        };
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

    /// <summary>
    /// Constrains snapshot output: requires a <c>.vdir</c> file target, refuses existing
    /// directories and Windows system directories. Returns an error message, or null when valid.
    /// </summary>
    internal static string? ValidateSnapshotPath(string outFile)
    {
        string full;
        try
        {
            full = Path.GetFullPath(outFile);
        }
        catch (Exception ex)
        {
            return $"Invalid --out path: {ex.Message}";
        }
        if (!full.EndsWith(".vdir", StringComparison.OrdinalIgnoreCase))
            return "The --out snapshot path must use a .vdir extension.";
        if (Directory.Exists(full))
            return "The --out path is an existing directory; point it at a .vdir file instead.";
        string? parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent))
            return "Invalid --out path.";
        if (IsProtectedSystemDirectory(parent))
            return "Refusing to write a snapshot into a Windows system directory.";
        return null;
    }

    internal static bool IsProtectedSystemDirectory(string directory)
    {
        string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string system = Environment.SystemDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return (!string.IsNullOrEmpty(windows) &&
            (full.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
             full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrEmpty(system) &&
            (full.Equals(system, StringComparison.OrdinalIgnoreCase) ||
             full.StartsWith(system + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            VisDir.Scanner — disk usage scanner
            Usage:
              VisDir.App --worker <path> [--mode auto|mft|generic] [--out <file.vdir>] [--report] [--top N] [--threads N] [--diff]

              path       directory or drive root to scan (e.g. C:\)
              --out      write binary snapshot file
              --report   print human-readable summary
              --top N    items in report (default 25)
              --threads N worker threads (default: CPU count)
              --diff     verify MFT against an independent Compatible scan
                         exit 0: pass; 4: mismatch; 5: inconclusive (partial/fallback/missing reference)
            """);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
