using System.Buffers.Binary;
using System.IO;

namespace VisDir.Core;

/// <summary>
/// Compact binary tree snapshot. Pre-order DFS; topology is explicit through child counts.
/// Layout v3 (reader also accepts v2):
///   magic "VDIR", u32 version
///   volume: rootPath, displayName, fsName (u16 len + utf8), u64 serial, u32 bytesPerCluster,
///           u64 totalBytes, u64 freeBytes
///   engineName, stats: i64 files, i64 dirs, i64 errors, u64 bytesSeen, f64 elapsedMs
///   u64 nodeCount, then per node: u16 flags, u16 reserved, i64 fileKey,
///           u64 logical, u64 allocated, u64 totalLogical, u64 totalAllocated,
///           u32 childCount, optional u64 identity/accounting fields selected by presence bits, name
/// </summary>
public static class TreeSerializer
{
    public const uint Magic = 0x52494456; // "VDIR" LE
    public const uint SnapshotVersion = 3;
    private const ulong MaxNodeCount = 100_000_000;

    public static void Write(Stream stream, ScanResult result, CancellationToken cancellationToken = default)
    {
        // BufferedStream coalesces the many small field writes into large sequential transfers.
        // It is intentionally never disposed: the caller's stream must stay open, so all exits
        // below flush explicitly instead. 256 KiB per snapshot-buffer contract.
        var buffered = new BufferedStream(stream, 1 << 18);
        using var bw = new BinaryWriter(buffered, System.Text.Encoding.UTF8, leaveOpen: true);

        bw.Write(Magic);
        bw.Write(SnapshotVersion);

        var v = result.Volume;
        WriteString(bw, v.RootPath);
        WriteString(bw, v.DisplayName);
        WriteString(bw, v.FileSystemName);
        bw.Write(v.VolumeSerialNumber);
        bw.Write(v.BytesPerCluster);
        bw.Write(v.TotalBytes);
        bw.Write(v.FreeBytes);

        WriteString(bw, result.EngineName);
        bw.Write(result.Stats.FileCount);
        bw.Write(result.Stats.DirectoryCount);
        bw.Write(result.Stats.ErrorCount);
        bw.Write(result.Stats.BytesSeen);
        bw.Write(result.Stats.ElapsedMs);
        WriteString(bw, result.RequestedEngine);
        WriteString(bw, result.FallbackReason ?? "");
        bw.Write((byte)result.Completeness);
        bw.Write((uint)result.AccountingFeatures);
        bw.Write((ushort)Math.Min(result.Issues.Count, 256));
        foreach (ScanIssue issue in result.Issues.Take(256))
        {
            WriteString(bw, issue.Path); WriteString(bw, issue.Reason);
        }
        bw.Write((ushort)Math.Min(result.Stats.PhaseMs.Count, 64));
        foreach (var phase in result.Stats.PhaseMs.Take(64))
        {
            WriteString(bw, phase.Key); bw.Write(phase.Value);
        }

        // Seekable streams patch a node count after the walk.
        // Non-seekable streams use a counting pre-pass.
        long nodeCountPos = -1;
        ulong upfrontCount = 0;
        if (stream.CanSeek)
        {
            nodeCountPos = bw.BaseStream.Position;
            bw.Write(0UL);
        }
        else
        {
            upfrontCount = CountNodes(result.Root, cancellationToken);
            bw.Write(upfrontCount);
        }

        ulong count = 0;
        // Pre-order with explicit stack (children pushed reversed to preserve sorted order).
        var stack = new Stack<FsNode>();
        stack.Push(result.Root);
        // One packed header per node: single stream write instead of eight tiny ones.
        Span<byte> header = stackalloc byte[48];
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FsNode n = stack.Pop();
            count++;

            // Fixed-size header, little-endian exactly as the former BinaryWriter sequence:
            // u16 flags, u16 reserved, i64 key, u64x4 sizes, u32 childCount. The transient
            // ChildrenSorted bit is masked out; v3 extends the v2 header with accounting/identity.
            BinaryPrimitives.WriteUInt16LittleEndian(header[..2], (ushort)(n.Flags & ~NodeFlags.ChildrenSorted));
            ushort components = (ushort)((n.FileKeyHigh != 0 ? 1 : 0) | (n.VolumeKey != v.VolumeSerialNumber ? 2 : 0)
                | (n.StreamAllocatedSize != 0 ? 4 : 0) | (n.MetadataAllocatedSize != 0 ? 8 : 0)
                | (n.TotalStreamAllocated != 0 ? 16 : 0) | (n.TotalMetadataAllocated != 0 ? 32 : 0));
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..4], components);
            BinaryPrimitives.WriteInt64LittleEndian(header[4..12], n.FileKey);
            BinaryPrimitives.WriteUInt64LittleEndian(header[12..20], n.LogicalSize);
            BinaryPrimitives.WriteUInt64LittleEndian(header[20..28], n.AllocatedSize);
            BinaryPrimitives.WriteUInt64LittleEndian(header[28..36], n.TotalLogical);
            BinaryPrimitives.WriteUInt64LittleEndian(header[36..44], n.TotalAllocated);
            BinaryPrimitives.WriteUInt32LittleEndian(header[44..48], (uint)(n.Children?.Count ?? 0));
            // BinaryWriter.BaseStream flushes its writer on every access. Writing through
            // the writer preserves batching instead of flushing twice for every node.
            bw.Write(header);
            if ((components & 1) != 0) bw.Write(n.FileKeyHigh);
            if ((components & 2) != 0) bw.Write(n.VolumeKey);
            if ((components & 4) != 0) bw.Write(n.StreamAllocatedSize);
            if ((components & 8) != 0) bw.Write(n.MetadataAllocatedSize);
            if ((components & 16) != 0) bw.Write(n.TotalStreamAllocated);
            if ((components & 32) != 0) bw.Write(n.TotalMetadataAllocated);
            WriteString(bw, n.Name);

            if (n.Children is { } kids)
                for (int i = kids.Count - 1; i >= 0; i--)
                    stack.Push(kids[i]);
        }

        if (stream.CanSeek)
        {
            long endPos = bw.BaseStream.Position;
            bw.BaseStream.Seek(nodeCountPos, SeekOrigin.Begin);
            bw.Write(count);
            bw.BaseStream.Seek(endPos, SeekOrigin.Begin);
        }
        else if (count != upfrontCount)
        {
            buffered.Flush();
            throw new InvalidDataException("Tree was modified while the snapshot was being written.");
        }
        bw.Flush();
        buffered.Flush();
    }

    private static ulong CountNodes(FsNode root, CancellationToken cancellationToken)
    {
        ulong count = 0;
        var stack = new Stack<FsNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FsNode n = stack.Pop();
            count++;
            if (n.Children is { } kids)
                for (int i = kids.Count - 1; i >= 0; i--)
                    stack.Push(kids[i]);
        }
        return count;
    }

    private static void WriteString(BinaryWriter bw, string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            bw.Write((ushort)0);
            return;
        }
        int maxLen = System.Text.Encoding.UTF8.GetMaxByteCount(s.Length);
        if (maxLen <= 256)
        {
            Span<byte> stackBuf = stackalloc byte[maxLen];
            int written = System.Text.Encoding.UTF8.GetBytes(s, stackBuf);
            bw.Write((ushort)written);
            bw.Write(stackBuf[..written]);
            return;
        }
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxLen);
        try
        {
            int written = System.Text.Encoding.UTF8.GetBytes(s, rented);
            if (written > ushort.MaxValue) throw new IOException("Node name too long.");
            bw.Write((ushort)written);
            bw.Write(rented, 0, written);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static ScanResult Read(Stream stream) => Read(stream, recomputeTotals: true);

    public static ScanResult Read(Stream stream, bool recomputeTotals, CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ReadCore(stream, recomputeTotals, cancellationToken, progress);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("VisDir snapshot is truncated.", ex);
        }
    }

    private static ScanResult ReadCore(Stream stream, bool recomputeTotals, CancellationToken cancellationToken,
        IProgress<ScanProgress>? progress)
    {
        long started = Environment.TickCount64;
        long lastReport = started;
        using var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        if (br.ReadUInt32() != Magic) throw new InvalidDataException("Not a VisDir snapshot.");
        uint version = br.ReadUInt32();
        if (version is not (2 or SnapshotVersion))
        {
            string detail = version == 1
                ? "Version 1 snapshots did not contain enough topology information and cannot be read safely."
                : $"Unsupported snapshot version {version}.";
            throw new InvalidDataException(detail);
        }

        string rootPath = ReadString(br);
        string displayName = ReadString(br);
        string fsName = ReadString(br);
        ulong serial = br.ReadUInt64();
        uint cluster = br.ReadUInt32();
        ulong total = br.ReadUInt64();
        ulong free = br.ReadUInt64();

        string engineName = ReadString(br);
        long fileCount = br.ReadInt64();
        long directoryCount = br.ReadInt64();
        long errorCount = br.ReadInt64();
        ulong bytesSeen = br.ReadUInt64();
        double elapsedMs = br.ReadDouble();
        if (fileCount < 0 || directoryCount < 0 || errorCount < 0)
            throw new InvalidDataException("Snapshot scan counts must be non-negative.");
        if (double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs) || elapsedMs < 0)
            throw new InvalidDataException("Snapshot elapsed time is invalid.");

        string requestedEngine = "auto";
        string? fallbackReason = null;
        ScanCompleteness completeness = errorCount > 0 ? ScanCompleteness.Partial : ScanCompleteness.Complete;
        AccountingFeatures features = AccountingFeatures.UnnamedData;
        var issues = new List<ScanIssue>();
        var phases = new Dictionary<string, double>();
        if (version >= 3)
        {
            requestedEngine = ReadString(br);
            fallbackReason = ReadString(br);
            if (fallbackReason.Length == 0) fallbackReason = null;
            completeness = (ScanCompleteness)br.ReadByte();
            features = (AccountingFeatures)br.ReadUInt32();
            if (!Enum.IsDefined(completeness) || ((uint)features & ~15U) != 0)
                throw new InvalidDataException("Invalid scan accounting metadata.");
            int issueCount = br.ReadUInt16();
            if (issueCount > 256) throw new InvalidDataException("Too many scan diagnostics.");
            for (int i = 0; i < issueCount; i++) issues.Add(new ScanIssue(ReadString(br), ReadString(br)));
            int phaseCount = br.ReadUInt16();
            if (phaseCount > 64) throw new InvalidDataException("Too many phase timings.");
            for (int i = 0; i < phaseCount; i++)
            {
                string phase = ReadString(br);
                double milliseconds = br.ReadDouble();
                if (!double.IsFinite(milliseconds) || milliseconds < 0 || !phases.TryAdd(phase, milliseconds))
                    throw new InvalidDataException("Invalid scan timing.");
            }
        }

        var volume = new VolumeInfo
        {
            RootPath = rootPath,
            DisplayName = displayName,
            FileSystemName = fsName,
            VolumeSerialNumber = serial,
            BytesPerCluster = cluster == 0 ? 4096 : cluster,
            TotalBytes = total,
            FreeBytes = free,
        };
        ulong nodeCount = br.ReadUInt64();
        if (nodeCount is 0 or > MaxNodeCount)
            throw new InvalidDataException($"Snapshot node count {nodeCount:N0} is invalid.");

        // These are the scan's known totals, not fabricated zero counts while loading.
        // Node progress covers transfer/deserialization; 100% is reserved for a fully
        // validated and frozen result. The transport owns any read-ahead buffer so
        // protocol frames and the snapshot always share the same buffered stream.
        void ReportLoading(ulong nodesRead, bool complete = false)
        {
            lastReport = Environment.TickCount64;
            progress?.Report(new ScanProgress(fileCount, directoryCount, bytesSeen,
                lastReport - started, complete ? 1 : Math.Min(0.99, (double)nodesRead / nodeCount), "Loading results"));
        }
        ReportLoading(0);

        ushort knownFlagsMask = 0;
        foreach (NodeFlags f in Enum.GetValues<NodeFlags>()) knownFlagsMask |= (ushort)f;

        FsNode? root = null;
        var parentStack = new Stack<(FsNode Node, uint RemainingChildren)>();
        for (ulong i = 0; i < nodeCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ushort flagsRaw = br.ReadUInt16();
            ushort components = br.ReadUInt16();
            if (version >= 3 && (components & ~63) != 0)
                throw new InvalidDataException("Unknown node accounting fields.");
            if ((flagsRaw & ~knownFlagsMask) != 0)
                throw new InvalidDataException($"Node {i:N0} has unknown flag bits (0x{flagsRaw:X4}).");
            long key = br.ReadInt64();
            ulong logical = br.ReadUInt64();
            ulong allocated = br.ReadUInt64();
            ulong totalLogical = br.ReadUInt64();
            ulong totalAllocated = br.ReadUInt64();
            uint childCount = br.ReadUInt32();
            if (childCount > int.MaxValue)
                throw new InvalidDataException($"Node {i:N0} has too many children.");
            ulong keyHigh = version >= 3 && (components & 1) != 0 ? br.ReadUInt64() : 0;
            ulong volumeKey = version >= 3 && (components & 2) != 0 ? br.ReadUInt64() : serial;
            ulong streams = version >= 3 && (components & 4) != 0 ? br.ReadUInt64() : 0;
            ulong metadata = version >= 3 && (components & 8) != 0 ? br.ReadUInt64() : 0;
            ulong totalStreams = version >= 3 && (components & 16) != 0 ? br.ReadUInt64() : 0;
            ulong totalMetadata = version >= 3 && (components & 32) != 0 ? br.ReadUInt64() : 0;
            if (streams > allocated || metadata > allocated - streams || totalStreams > totalAllocated
                || totalMetadata > totalAllocated - totalStreams)
                throw new InvalidDataException("Allocation components exceed the node allocation.");
            string name = ReadString(br);

            var node = new FsNode
            {
                Name = name,
                // The ChildrenSorted bit is a transient in-memory hint and is never trusted from disk.
                Flags = (NodeFlags)(flagsRaw & ~(ushort)NodeFlags.ChildrenSorted),
                FileKey = key,
                FileKeyHigh = keyHigh,
                VolumeKey = volumeKey,
                StreamAllocatedSize = streams,
                MetadataAllocatedSize = metadata,
                TotalStreamAllocated = totalStreams,
                TotalMetadataAllocated = totalMetadata,
                LogicalSize = logical,
                AllocatedSize = allocated,
                TotalLogical = totalLogical,
                TotalAllocated = totalAllocated,
            };

            if (parentStack.Count == 0)
            {
                if (root is not null)
                    throw new InvalidDataException("Snapshot contains more than one root node.");
                root = node;
            }
            else
            {
                var parent = parentStack.Pop();
                parent.Node.AddChild(node);
                if (parent.RemainingChildren == 0)
                    throw new InvalidDataException("Snapshot topology contains an overfull parent.");
                parent.RemainingChildren--;
                if (parent.RemainingChildren > 0) parentStack.Push(parent);
            }

            if (childCount > 0)
            {
                // Snapshot bomb guard: childCount is bounded by nodes left minus what
                // ancestors already owe, so a huge count can't preallocate memory.
                ulong remaining = nodeCount - i - 1;
                ulong stackOwed = 0;
                foreach ((FsNode _, uint owed) in parentStack) stackOwed += owed;
                if (stackOwed > remaining || childCount > remaining - stackOwed)
                    throw new InvalidDataException($"Node {i:N0} has more children than nodes left.");
                // Declared up front: presize once so the child batch appends without regrowth.
                // Capped to remaining (int-safe: childCount already <= int.MaxValue above).
                node.EnsureCapacity((int)Math.Min((ulong)childCount, remaining));
                parentStack.Push((node, childCount));
            }
            if (progress is not null && (i & 1023) == 1023 && Environment.TickCount64 - lastReport >= 100)
                ReportLoading(i + 1);
        }

        if (root is null) throw new InvalidDataException("Snapshot contains no nodes.");
        if (parentStack.Count != 0)
            throw new InvalidDataException("Snapshot ended before all declared children were read.");

        // Stored Total* values are not trusted: recompute them from self sizes (overwrite on
        // drift) so corrupt totals can never surface, and re-establish TotalAllocated ordering.
        // Skipped when the caller trusts the stored aggregates; topology above is validated either way.
        if (recomputeTotals)
            TreeOps.Finalize(root, cancellationToken);
        TreeOps.Freeze(root, cancellationToken);
        ReportLoading(nodeCount, complete: true);

        return new ScanResult
        {
            Volume = volume,
            Root = root,
            EngineName = engineName,
            RequestedEngine = requestedEngine,
            FallbackReason = fallbackReason,
            Completeness = completeness,
            AccountingFeatures = features,
            Issues = issues.AsReadOnly(),
            Stats = new ScanStats
            {
                FileCount = fileCount,
                DirectoryCount = directoryCount,
                ErrorCount = errorCount,
                BytesSeen = bytesSeen,
                ElapsedMs = elapsedMs,
                PhaseMs = new System.Collections.ObjectModel.ReadOnlyDictionary<string, double>(phases),
            },
        };
    }

    private static string ReadString(BinaryReader br)
    {
        ushort len = br.ReadUInt16();
        if (len == 0) return string.Empty;
        if (len <= 256)
        {
            Span<byte> stackBuf = stackalloc byte[len];
            br.BaseStream.ReadExactly(stackBuf);
            return System.Text.Encoding.UTF8.GetString(stackBuf);
        }
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(len);
        try
        {
            br.BaseStream.ReadExactly(rented.AsSpan(0, len));
            return System.Text.Encoding.UTF8.GetString(rented, 0, len);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
