using System.IO;

namespace VisDir.Core;

/// <summary>
/// Compact binary tree snapshot. Pre-order DFS; topology is explicit through child counts.
/// Layout v2:
///   magic "VDIR", u32 version
///   volume: rootPath, displayName, fsName (u16 len + utf8), u64 serial, u32 bytesPerCluster,
///           u64 totalBytes, u64 freeBytes
///   engineName, stats: i64 files, i64 dirs, i64 errors, u64 bytesSeen, f64 elapsedMs
///   u64 nodeCount, then per node: u16 flags, u16 reserved, i64 fileKey,
///           u64 logical, u64 allocated, u64 totalLogical, u64 totalAllocated,
///           u32 childCount, u16 nameLen + utf8 name
/// </summary>
public static class TreeSerializer
{
    public const uint Magic = 0x52494456; // "VDIR" LE
    public const uint Version = 2;
    private const ulong MaxNodeCount = 100_000_000;

    public static void Write(Stream stream, ScanResult result)
    {
        using var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        bw.Write(Magic);
        bw.Write(Version);

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

        long nodeCountPos = bw.BaseStream.Position;
        bw.Write(0UL);

        ulong count = 0;
        // Pre-order with explicit stack (children pushed reversed to preserve sorted order).
        var stack = new Stack<FsNode>();
        stack.Push(result.Root);
        while (stack.Count > 0)
        {
            FsNode n = stack.Pop();
            count++;

            bw.Write((ushort)n.Flags);
            bw.Write((ushort)0);
            bw.Write(n.FileKey);
            bw.Write(n.LogicalSize);
            bw.Write(n.AllocatedSize);
            bw.Write(n.TotalLogical);
            bw.Write(n.TotalAllocated);
            bw.Write((uint)(n.Children?.Count ?? 0));
            WriteString(bw, n.Name);

            if (n.Children is { } kids)
                for (int i = kids.Count - 1; i >= 0; i--)
                    stack.Push(kids[i]);
        }

        long endPos = bw.BaseStream.Position;
        bw.BaseStream.Seek(nodeCountPos, SeekOrigin.Begin);
        bw.Write(count);
        bw.BaseStream.Seek(endPos, SeekOrigin.Begin);
        bw.Flush();
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
            bw.BaseStream.Write(stackBuf[..written]);
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

    public static ScanResult Read(Stream stream)
    {
        try
        {
            return ReadCore(stream);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("VisDir snapshot is truncated.", ex);
        }
    }

    private static ScanResult ReadCore(Stream stream)
    {
        using var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        if (br.ReadUInt32() != Magic) throw new InvalidDataException("Not a VisDir snapshot.");
        uint version = br.ReadUInt32();
        if (version != Version)
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

        FsNode? root = null;
        var parentStack = new Stack<(FsNode Node, uint RemainingChildren)>();
        for (ulong i = 0; i < nodeCount; i++)
        {
            ushort flagsRaw = br.ReadUInt16();
            br.ReadUInt16(); // reserved
            long key = br.ReadInt64();
            ulong logical = br.ReadUInt64();
            ulong allocated = br.ReadUInt64();
            ulong totalLogical = br.ReadUInt64();
            ulong totalAllocated = br.ReadUInt64();
            uint childCount = br.ReadUInt32();
            if (childCount > int.MaxValue)
                throw new InvalidDataException($"Node {i:N0} has too many children.");
            string name = ReadString(br);

            var node = new FsNode
            {
                Name = name,
                Flags = (NodeFlags)flagsRaw,
                FileKey = key,
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

            if (childCount > 0) parentStack.Push((node, childCount));
        }

        if (root is null) throw new InvalidDataException("Snapshot contains no nodes.");
        if (parentStack.Count != 0)
            throw new InvalidDataException("Snapshot ended before all declared children were read.");

        return new ScanResult
        {
            Volume = volume,
            Root = root,
            EngineName = engineName,
            Stats = new ScanStats
            {
                FileCount = fileCount,
                DirectoryCount = directoryCount,
                ErrorCount = errorCount,
                BytesSeen = bytesSeen,
                ElapsedMs = elapsedMs,
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
