using System.Buffers.Binary;

namespace VisDir.Core.Scanning;

/// <summary>
/// Bootstraps the MFT's run list from record zero and its attribute list. Every byte
/// in the requested MFT range must be mapped; unsupported/corrupt layouts fail closed.
/// The physical reader is injectable so fragmented layouts can be tested without a volume.
/// </summary>
internal static class MftExtentReader
{
    internal readonly record struct Extent(ulong Vcn, long Lcn, ulong Clusters);
    private readonly record struct Reference(ulong Vcn, ulong FileReference, ushort AttributeId);
    private sealed record Layout(List<Extent> Data, List<Reference> References, ushort Sequence,
        ulong BaseReference, bool InUse);

    internal static List<(long StartLcn, ulong Clusters)> Discover(
        Func<ulong, int, byte[]> readPhysical, uint clusterSize, int recordSize,
        ulong startLcn, ulong totalClusters, ulong validLength, CancellationToken ct)
    {
        if (clusterSize == 0 || startLcn >= totalClusters || validLength == 0
            || validLength % (ulong)recordSize != 0)
            throw new IOException("Invalid MFT geometry or length.");
        ulong volumeLength = checked(totalClusters * clusterSize);
        if (validLength > volumeLength) throw new IOException("MFT exceeds the volume length.");

        Layout first = ParseRecord(ReadPhysical(checked(startLcn * clusterSize), recordSize));
        if (!first.InUse || first.BaseReference != 0) throw new IOException("Invalid MFT base record.");
        var mappings = new SortedDictionary<ulong, Extent>();
        AddMappings(first.Data);
        var pending = new List<Reference>(first.References);
        var readRecords = new HashSet<ulong> { 0 };
        var sequenceByRecord = new Dictionary<ulong, ushort> { [0] = first.Sequence };
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            bool advanced = false;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Reference reference = pending[i];
                ulong number = reference.FileReference & 0x0000FFFFFFFFFFFFUL;
                ushort sequence = (ushort)(reference.FileReference >> 48);
                if (readRecords.Contains(number))
                {
                    if (sequence != 0 && sequenceByRecord[number] != sequence)
                        throw new IOException("Stale MFT attribute-list reference.");
                    if (!mappings.ContainsKey(reference.Vcn))
                        throw new IOException("MFT attribute-list extent is missing.");
                    pending.RemoveAt(i);
                    advanced = true;
                    continue;
                }
                ulong offset = checked(number * (ulong)recordSize);
                if (offset > validLength || (ulong)recordSize > validLength - offset)
                    throw new IOException("MFT extension reference is outside the MFT.");
                if (!TryReadLogical(mappings.Values, offset, recordSize, out byte[]? bytes)) continue;
                Layout extension = ParseRecord(bytes!);
                if (!extension.InUse || extension.BaseReference == 0
                    || (extension.BaseReference & 0x0000FFFFFFFFFFFFUL) != 0
                    || (sequence != 0 && extension.Sequence != sequence)
                    || ((extension.BaseReference >> 48) != 0 && (extension.BaseReference >> 48) != first.Sequence))
                    throw new IOException("Invalid MFT extension record reference.");
                AddMappings(extension.Data);
                readRecords.Add(number);
                sequenceByRecord[number] = extension.Sequence;
                pending.RemoveAt(i);
                pending.AddRange(extension.References);
                advanced = true;
            }
            if (!advanced) throw new IOException("Cannot safely locate all MFT extension records.");
        }

        ulong covered = 0;
        var result = new List<(long, ulong)>(mappings.Count);
        foreach (Extent extent in mappings.Values)
        {
            if (extent.Vcn != covered) throw new IOException("MFT run list has a gap or overlap.");
            result.Add((extent.Lcn, extent.Clusters));
            covered = checked(covered + extent.Clusters);
        }
        if (covered < (validLength + clusterSize - 1) / clusterSize)
            throw new IOException("MFT run list does not cover its valid data length.");
        return result;

        byte[] ReadPhysical(ulong offset, int length)
        {
            ct.ThrowIfCancellationRequested();
            if (offset > volumeLength || (ulong)length > volumeLength - offset)
                throw new IOException("MFT run is outside the volume.");
            byte[] bytes = readPhysical(offset, length);
            if (bytes.Length != length) throw new IOException("Incomplete MFT metadata read.");
            return bytes;
        }

        void AddMappings(IEnumerable<Extent> extents)
        {
            foreach (Extent extent in extents)
            {
                if (mappings.TryGetValue(extent.Vcn, out Extent existing) && existing != extent)
                    throw new IOException("Conflicting MFT extents.");
                mappings[extent.Vcn] = extent;
            }
        }

        bool TryReadLogical(IEnumerable<Extent> extents, ulong offset, int length, out byte[]? resultBytes)
        {
            var output = new byte[length];
            int done = 0;
            foreach (Extent extent in extents.OrderBy(e => e.Vcn))
            {
                ulong begin = checked(extent.Vcn * clusterSize);
                ulong end = checked(begin + extent.Clusters * clusterSize);
                if (offset < begin) break;
                if (offset >= end) continue;
                int count = (int)Math.Min((ulong)(length - done), end - offset);
                byte[] part = ReadPhysical(checked((ulong)extent.Lcn * clusterSize + offset - begin), count);
                part.CopyTo(output, done);
                done += count;
                offset += (ulong)count;
                if (done == length) { resultBytes = output; return true; }
            }
            resultBytes = null;
            return false;
        }

        Layout ParseRecord(byte[] record)
        {
            ApplyFixup(record);
            var data = new List<Extent>();
            var references = new List<Reference>();
            int used = checked((int)U32(record, 0x18));
            int offset = U16(record, 0x14);
            if (used > record.Length || offset < 0x30 || offset > used)
                throw new IOException("Invalid MFT record bounds.");
            bool ended = false;
            while (offset <= used - 4)
            {
                uint type = U32(record, offset);
                if (type == 0xffffffff) { ended = true; break; }
                if (offset > used - 0x10) throw new IOException("Truncated MFT attribute header.");
                uint rawLength = U32(record, offset + 4);
                if (rawLength < 0x18 || rawLength > used - offset || (rawLength & 7) != 0)
                    throw new IOException("Invalid MFT attribute length.");
                var attribute = record.AsSpan(offset, (int)rawLength);
                bool nonResident = attribute[8] != 0;
                bool unnamed = attribute[9] == 0;
                if (type == 0x80 && nonResident && unnamed)
                    data.AddRange(DecodeRuns(attribute, totalClusters));
                else if (type == 0x20)
                {
                    byte[] list;
                    if (nonResident)
                    {
                        List<Extent> runs = DecodeRuns(attribute, totalClusters);
                        ulong size = U64(attribute, 0x30);
                        // A bounded metadata allocation; larger/continuation layouts use fallback.
                        if (size > 64 * 1024 * 1024 || !TryReadLogical(runs, 0, (int)size, out byte[]? content))
                            throw new IOException("Unsupported or incomplete MFT attribute list.");
                        list = content!;
                    }
                    else
                    {
                        uint length = U32(attribute, 0x10);
                        ushort start = U16(attribute, 0x14);
                        if (start < 0x18 || start > attribute.Length || length > attribute.Length - start)
                            throw new IOException("Invalid resident MFT attribute list.");
                        list = attribute.Slice(start, (int)length).ToArray();
                    }
                    ReadReferences(list, references);
                }
                offset += (int)rawLength;
            }
            if (!ended) throw new IOException("Unterminated MFT attributes.");
            return new Layout(data, references, U16(record, 0x10), U64(record, 0x20), (U16(record, 0x16) & 1) != 0);
        }
    }

    private static void ReadReferences(ReadOnlySpan<byte> list, List<Reference> references)
    {
        int offset = 0;
        while (offset < list.Length)
        {
            if (list.Length - offset < 0x1a) throw new IOException("Truncated MFT attribute-list entry.");
            var item = list[offset..];
            ushort length = U16(item, 4);
            if (length < 0x1a || length > item.Length) throw new IOException("Invalid MFT attribute-list entry.");
            if (U32(item, 0) == 0x80 && item[6] == 0)
                references.Add(new Reference(U64(item, 8), U64(item, 0x10), U16(item, 0x18)));
            offset += length;
        }
    }

    internal static List<Extent> DecodeRuns(ReadOnlySpan<byte> attribute, ulong totalClusters)
    {
        if (attribute.Length < 0x40) throw new IOException("Truncated nonresident MFT attribute.");
        ulong vcn = U64(attribute, 0x10), highest = U64(attribute, 0x18);
        int offset = U16(attribute, 0x20);
        if (offset < 0x40 || offset >= attribute.Length) throw new IOException("Invalid MFT mapping-pairs offset.");
        var result = new List<Extent>();
        long lcn = 0;
        bool ended = false;
        while (offset < attribute.Length)
        {
            byte header = attribute[offset++];
            if (header == 0) { ended = true; break; }
            int lengths = header & 15, deltas = header >> 4;
            if (lengths is < 1 or > 8 || deltas is < 1 or > 8 || lengths + deltas > attribute.Length - offset)
                throw new IOException("Invalid or sparse MFT mapping pair.");
            ulong count = 0, rawDelta = 0;
            for (int i = 0; i < lengths; i++) count |= (ulong)attribute[offset++] << (i * 8);
            for (int i = 0; i < deltas; i++) rawDelta |= (ulong)attribute[offset++] << (i * 8);
            if (deltas < 8 && (rawDelta & (1UL << (deltas * 8 - 1))) != 0)
                rawDelta |= ulong.MaxValue << (deltas * 8);
            lcn = checked(lcn + unchecked((long)rawDelta));
            if (count == 0 || lcn < 0 || (ulong)lcn >= totalClusters || count > totalClusters - (ulong)lcn)
                throw new IOException("MFT mapping pair is outside the volume.");
            result.Add(new Extent(vcn, lcn, count));
            vcn = checked(vcn + count);
        }
        if (!ended || result.Count == 0 || highest == ulong.MaxValue || vcn != highest + 1)
            throw new IOException("Incomplete MFT mapping pairs.");
        return result;
    }

    internal static void ApplyFixup(Span<byte> record)
    {
        if (record.Length < 0x30 || !NtfsRecordParser.LooksLikeFileRecord(record))
            throw new IOException("Invalid MFT record signature.");
        int start = U16(record, 4), count = U16(record, 6);
        if (count < 2 || start < 0x30 || start + count * 2 > record.Length || record.Length % (count - 1) != 0)
            throw new IOException("Invalid MFT update sequence.");
        int stride = record.Length / (count - 1);
        if (stride < 512 || !int.IsPow2(stride)) throw new IOException("Invalid MFT sector stride.");
        ushort sequence = U16(record, start);
        for (int i = 1; i < count; i++)
        {
            int tail = i * stride - 2;
            if (U16(record, tail) != sequence) throw new IOException("Torn MFT record.");
            BinaryPrimitives.WriteUInt16LittleEndian(record[tail..], U16(record, start + i * 2));
        }
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
}
