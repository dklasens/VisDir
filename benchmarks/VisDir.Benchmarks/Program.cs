using System.Diagnostics;
using VisDir.Core;
using VisDir.Core.Scanning;

namespace VisDir.Benchmarks;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (TryArgument(args, "--mftimage", out string? imagePath)) return RunMftBench(imagePath, args);
        if (HasFlag(args, "--mftbench")) return RunMftBench(null, args);
        if (TryArgument(args, "--scan", out string? scanPath)) return RunScan(scanPath!);
        int nodes = TryArgument(args, "--nodes", out string? raw) && int.TryParse(raw, out int parsed)
            ? Math.Clamp(parsed, 1_000, 5_000_000)
            : 250_000;
        return RunSynthetic(nodes);
    }

    private static int RunSynthetic(int nodeCount)
    {
        Console.WriteLine($"VisDir synthetic benchmark · {nodeCount:N0} nodes");
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        FsNode root = BuildTree(nodeCount);
        clock.Stop();
        Print("construct", nodeCount, clock.Elapsed, 0);

        clock.Restart();
        TreeOps.Finalize(root);
        clock.Stop();
        Print("finalize", nodeCount, clock.Elapsed, 0);

        var result = new ScanResult
        {
            Root = root,
            Volume = new VolumeInfo { RootPath = "C:\\", DisplayName = "Benchmark", FileSystemName = "NTFS" },
            EngineName = "benchmark",
            Stats = new ScanStats { FileCount = nodeCount, DirectoryCount = 1 },
        };
        using var snapshot = new MemoryStream(capacity: Math.Min(nodeCount * 80, 1_000_000_000));
        clock.Restart();
        TreeSerializer.Write(snapshot, result);
        clock.Stop();
        Print("serialize", nodeCount, clock.Elapsed, snapshot.Length);

        snapshot.Position = 0;
        clock.Restart();
        ScanResult loaded = TreeSerializer.Read(snapshot);
        clock.Stop();
        Print("deserialize", nodeCount, clock.Elapsed, snapshot.Length);
        GC.KeepAlive(loaded);

        long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        Console.WriteLine($"managed allocated: {SizeFormatter.Format((ulong)allocated)}");
        return 0;
    }

    private static FsNode BuildTree(int target)
    {
        var root = new FsNode { Name = "benchmark", Flags = NodeFlags.Directory };
        var directories = new List<FsNode> { root };
        for (int i = 1; i < target; i++)
        {
            FsNode parent = directories[(i / 64) % directories.Count];
            if (i % 16 == 0)
            {
                var directory = new FsNode { Name = $"dir-{i}", Flags = NodeFlags.Directory };
                parent.AddChild(directory);
                directories.Add(directory);
            }
            else
            {
                parent.AddChild(new FsNode
                {
                    Name = $"file-{i}.bin", LogicalSize = (ulong)(i % 1_000_000),
                    AllocatedSize = (ulong)((i % 256) + 1) * 4096,
                });
            }
        }
        return root;
    }

    /// <summary>
    /// Splits MFT throughput into read time vs <see cref="NtfsRecordParser.TryParseRecord"/>
    /// CPU time so the native-parser gate (parse share &gt; 30%) is measurable.
    /// Synthetic mode times a memcpy proxy for the read side (same bytes, no kernel I/O:
    /// an I/O lower bound, hence an upper bound on parse share); --mftimage times a real
    /// FileStream read of captured bytes. Parse passes run over a work copy because the
    /// update-sequence fixup mutates records in place, exactly like the scanner's chunk loop.
    /// </summary>
    private static int RunMftBench(string? imagePath, string[] args)
    {
        int recordSize = IntArgument(args, "--recordsize", 1024, 512, 4096);
        if (!int.IsPow2(recordSize)) recordSize = 1 << (31 - int.LeadingZeroCount(recordSize));
        int iters = IntArgument(args, "--iters", 3, 1, 10);

        byte[] pristine;
        double readMsPerPass;
        string source;
        if (imagePath is not null)
        {
            string full = Path.GetFullPath(imagePath);
            var readClock = Stopwatch.StartNew();
            pristine = ReadFileBytes(full);
            readClock.Stop();
            readMsPerPass = readClock.Elapsed.TotalMilliseconds;
            source = $"image {full} (1 read pass)";
        }
        else
        {
            int records = IntArgument(args, "--records", 100_000, 1_000, 1_000_000);
            while ((long)records * recordSize > 1_000_000_000) records /= 2;
            pristine = BuildMftImage(records, recordSize);
            source = "synthetic (memcpy read proxy)";
            readMsPerPass = 0;
        }

        long bytesPerPass = (long)(pristine.Length / recordSize) * recordSize;
        long recordsPerPass = bytesPerPass / recordSize;
        if (recordsPerPass == 0) { Console.Error.WriteLine("No whole records to parse."); return 1; }
        Console.WriteLine($"VisDir MFT parse-vs-read benchmark · {recordsPerPass:N0} records × {recordSize:N0} B = {SizeFormatter.Format((ulong)bytesPerPass)} ({source})");

        var work = new byte[pristine.Length];
        long ok = 0, fixupFail = 0, structFail = 0, nameChars = 0;
        double readMsTotal = imagePath is null ? 0 : readMsPerPass;
        double parseMsTotal = 0;
        // Dictionary-insert sink: mirrors the scanner's entries map so the measured
        // parse region includes tree-assembly insert cost, not just record decoding.
        var buildSink = new Dictionary<long, MftEntryInfo>((int)Math.Min(recordsPerPass, 1_000_000));
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        unsafe
        {
            fixed (byte* workPtr = work)
            {
                for (int i = 0; i < iters; i++)
                {
                    if (imagePath is null)
                    {
                        var copyClock = Stopwatch.StartNew();
                        Buffer.BlockCopy(pristine, 0, work, 0, pristine.Length);
                        copyClock.Stop();
                        readMsTotal += copyClock.Elapsed.TotalMilliseconds;
                    }
                    else
                    {
                        Buffer.BlockCopy(pristine, 0, work, 0, pristine.Length); // refresh fixup slots; not timed
                    }

                    buildSink.Clear();
                    var parseClock = Stopwatch.StartNew();
                    (ok, fixupFail, structFail, nameChars) = ParseBuffer(workPtr, bytesPerPass, recordSize, buildSink);
                    parseClock.Stop();
                    parseMsTotal += parseClock.Elapsed.TotalMilliseconds;
                }
            }
        }
        long allocated = (long)GC.GetTotalAllocatedBytes(false) - allocatedBefore;
        GC.KeepAlive(pristine);
        GC.KeepAlive(work);

        double readMs = readMsTotal / (imagePath is null ? iters : 1);
        double parseMs = parseMsTotal / iters;
        double readMbPerSec = (bytesPerPass / 1_048_576.0) / Math.Max(readMs / 1000.0, 1e-9);
        double parseMbPerSec = (bytesPerPass / 1_048_576.0) / Math.Max(parseMs / 1000.0, 1e-9);
        double recordsPerSec = recordsPerPass / Math.Max(parseMs / 1000.0, 1e-9);
        double nsPerRecord = parseMs * 1_000_000.0 / recordsPerPass;
        double parseShare = parseMs / Math.Max(readMs + parseMs, 1e-9) * 100.0;
        Console.WriteLine($"read    {readMs,9:N1} ms · {readMbPerSec,8:N0} MB/s");
        Console.WriteLine($"parse   {parseMs,9:N1} ms · {recordsPerSec,8:N0} records/s · {nsPerRecord:N0} ns/record · {parseMbPerSec:N0} MB/s (includes build dictionary inserts, entries={buildSink.Count:N0})");
        Console.WriteLine($"parse share: {parseShare:N1}% of read+parse");
        Console.WriteLine($"managed allocated in parse: {SizeFormatter.Format((ulong)Math.Max(allocated, 0))} ({(double)Math.Max(allocated, 0) / (recordsPerPass * iters):N1} B/record/pass)");
        Console.WriteLine($"parsed={ok:N0} fixupFail={fixupFail:N0} structFail={structFail:N0} nameChars={nameChars:N0}");
        // Per-phase timing for the tree pipeline on a small synthetic tree, so
        // parse/build/finalize/serialize are visible in one run. Gate unchanged.
        FsNode phaseRoot = BuildTree(20_000);
        var phaseClock = Stopwatch.StartNew();
        TreeOps.Finalize(phaseRoot);
        phaseClock.Stop();
        Print("finalize", 20_000, phaseClock.Elapsed, 0);
        var phaseResult = new ScanResult
        {
            Root = phaseRoot,
            Volume = new VolumeInfo { RootPath = "C:\\", DisplayName = "Benchmark", FileSystemName = "NTFS" },
            EngineName = "benchmark",
            Stats = new ScanStats { FileCount = 20_000, DirectoryCount = 1 },
        };
        using var phaseStream = new MemoryStream();
        phaseClock.Restart();
        TreeSerializer.Write(phaseStream, phaseResult);
        phaseClock.Stop();
        Print("serialize", 20_000, phaseClock.Elapsed, phaseStream.Length);
        Console.WriteLine($"build   entries={buildSink.Count:N0} (dictionary inserts inside parse timing above)");
        Console.WriteLine($"native-parser gate: parse share {parseShare:N1}% vs 30% threshold -> {(parseShare > 30 ? "GO (profile further)" : "NO-GO")}");
        return 0;
    }

    /// <summary>Mirrors the scanner's chunk-parse loop plus tree-assembly dictionary inserts:
    /// fast-path the magic byte, then parse and insert into the entries map so the measured
    /// region covers both parse and build work. Touches each name length so the parse work
    /// cannot be discarded.</summary>
    private static unsafe (long Ok, long FixupFail, long StructFail, long NameChars) ParseBuffer(
        byte* basePtr, long totalBytes, int recordSize, Dictionary<long, MftEntryInfo> sink)
    {
        long ok = 0, fixupFail = 0, structFail = 0, nameChars = 0;
        for (long off = 0; off + recordSize <= totalBytes; off += recordSize)
        {
            byte* rec = basePtr + off;
            if (*rec != (byte)'F') continue; // unused/corrupt slot fast-path, as in NtfsMftScanner
            if (NtfsRecordParser.TryParseRecord(rec, recordSize, out MftEntryInfo info, out byte stage))
            {
                ok++;
                sink[off / recordSize] = info;
                if (info.HasFileName) nameChars += info.Name.Length;
            }
            else if (stage == NtfsRecordParser.FailFixup) fixupFail++;
            else if (stage == NtfsRecordParser.FailStructure) structFail++;
        }
        return (ok, fixupFail, structFail, nameChars);
    }

    private static byte[] BuildMftImage(int records, int recordSize)
    {
        var image = new byte[records * recordSize];
        for (int i = 0; i < records; i++)
            WriteSyntheticRecord(image.AsSpan(i * recordSize, recordSize), i);
        return image;
    }

    /// <summary>Minimal but representative FILE record: header + resident $FILE_NAME +
    /// small resident $DATA, closed by the end marker, with a valid update sequence.</summary>
    private static void WriteSyntheticRecord(Span<byte> rec, int index)
    {
        rec.Clear();
        int sectors = rec.Length / 512;
        ushort usaCount = (ushort)(sectors + 1);
        const ushort usaOffset = 0x30;
        int attrsOffset = (usaOffset + usaCount * 2 + 7) & ~7;

        "FILE"u8.CopyTo(rec);
        BitConverter.TryWriteBytes(rec.Slice(4), usaOffset);
        BitConverter.TryWriteBytes(rec.Slice(6), usaCount);
        BitConverter.TryWriteBytes(rec.Slice(0x14), (ushort)attrsOffset);
        // 5% deleted (not in use) + 2% extension records, mirroring real MFT mix.
        bool isDeleted = index % 20 == 0;
        bool isExtension = !isDeleted && index % 50 == 1;
        ushort flags = isDeleted ? (ushort)0x0000 : (ushort)(index % 16 == 0 ? 0x0003 : 0x0001);
        BitConverter.TryWriteBytes(rec.Slice(0x16), flags);
        BitConverter.TryWriteBytes(rec.Slice(0x20), isExtension ? (ulong)(index - 1) : 0UL); // base reference: non-zero marks extensions
        BitConverter.TryWriteBytes(rec.Slice(0x2C), (uint)index);

        string name = $"file-{index}.bin";
        int valueLen = 0x42 + name.Length * 2;
        int fnAttrLen = (0x18 + valueLen + 7) & ~7;
        int off = attrsOffset;
        BitConverter.TryWriteBytes(rec.Slice(off), 0x30u); // $FILE_NAME
        BitConverter.TryWriteBytes(rec.Slice(off + 4), (uint)fnAttrLen);
        BitConverter.TryWriteBytes(rec.Slice(off + 0x10), (uint)valueLen);
        BitConverter.TryWriteBytes(rec.Slice(off + 0x14), (ushort)0x18);
        Span<byte> v = rec.Slice(off + 0x18, valueLen);
        BitConverter.TryWriteBytes(v, (ulong)(index / 64)); // parent record
        v[0x40] = (byte)name.Length;
        v[0x41] = (byte)(index % 8 == 7 ? 2 : 1);
        for (int c = 0; c < name.Length; c++)
            BitConverter.TryWriteBytes(v.Slice(0x42 + c * 2), name[c]);
        off += fnAttrLen;

        int room = rec.Length - off - 8; // end marker
        if (room >= 0x20)
        {
            uint dataLen = (uint)Math.Min(index % 700, room - 0x18);
            int dataAttrLen = ((0x18 + (int)dataLen) + 7) & ~7;
            BitConverter.TryWriteBytes(rec.Slice(off), 0x80u); // $DATA resident, unnamed
            BitConverter.TryWriteBytes(rec.Slice(off + 4), (uint)dataAttrLen);
            BitConverter.TryWriteBytes(rec.Slice(off + 0x10), dataLen);
            BitConverter.TryWriteBytes(rec.Slice(off + 0x14), (ushort)0x18);
            off += dataAttrLen;
        }

        BitConverter.TryWriteBytes(rec.Slice(off), 0xFFFF_FFFFu);
        BitConverter.TryWriteBytes(rec.Slice(0x18), (uint)(off + 8));

        // Update sequence: stash the true tail bytes, plant the sequence word.
        const ushort sequence = 0xA55A;
        Span<byte> usa = rec.Slice(usaOffset, usaCount * 2);
        for (int s = 1; s <= sectors; s++)
        {
            int slot = s * 512 - 2;
            usa[s * 2] = rec[slot];
            usa[s * 2 + 1] = rec[slot + 1];
            rec[slot] = (byte)(sequence & 0xFF);
            rec[slot + 1] = (byte)(sequence >> 8);
        }
        BitConverter.TryWriteBytes(usa, sequence);
    }

    private static byte[] ReadFileBytes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.SequentialScan);
        long length = fs.Length;
        if (length > int.MaxValue) throw new IOException($"Image too large: {length} bytes.");
        var bytes = new byte[(int)length];
        int done = 0;
        while (done < bytes.Length)
        {
            int got = fs.Read(bytes, done, bytes.Length - done);
            if (got == 0) break;
            done += got;
        }
        if (done != bytes.Length) Array.Resize(ref bytes, done);
        return bytes;
    }

    private static int IntArgument(string[] args, string name, int def, int min, int max)
        => TryArgument(args, name, out string? raw) && int.TryParse(raw, out int parsed)
            ? Math.Clamp(parsed, min, max) : def;

    private static bool HasFlag(string[] args, string name)
        => Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static int RunScan(string path)
    {
        path = Path.GetFullPath(path);
        Console.WriteLine($"VisDir compatible scanner benchmark · {path}");
        var progress = new InlineProgress<ScanProgress>(p =>
            Console.Write($"\r{p.FilesSeen:N0} files · {p.DirsSeen:N0} folders · {p.ElapsedMs / 1000:0.0}s"));
        ScanResult result = new GenericScanner().Scan(
            new ScanOptions { Path = path }, CancellationToken.None, progress);
        Console.WriteLine();
        Console.WriteLine($"{result.Stats.FileCount:N0} files · {SizeFormatter.Format(result.Root.TotalAllocated)} · {result.Stats.ElapsedMs / 1000:0.000}s");
        return 0;
    }

    private static void Print(string operation, int nodes, TimeSpan elapsed, long bytes)
    {
        double throughput = nodes / Math.Max(elapsed.TotalSeconds, 0.000_001);
        string suffix = bytes > 0 ? $" · {SizeFormatter.Format((ulong)bytes)} snapshot" : "";
        Console.WriteLine($"{operation,-12} {elapsed.TotalMilliseconds,9:N1} ms · {throughput,13:N0} nodes/s{suffix}");
    }

    private static bool TryArgument(string[] args, string name, out string? value)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        value = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        return value is not null;
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
