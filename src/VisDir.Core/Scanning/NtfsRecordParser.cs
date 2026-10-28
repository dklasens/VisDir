using System.Diagnostics.CodeAnalysis;

namespace VisDir.Core.Scanning;

public readonly record struct MftNameInfo(ulong ParentRecordNumber, string Name);

/// <summary>Flat snapshot of one parsed MFT file record segment.</summary>
public struct MftEntryInfo
{
    public long RecordNumber;
    public ushort SequenceNumber;
    public long BaseRecordNumber;          // 0 for base records
    public bool IsExtension;               // preserves an extension's reference to record zero ($MFT)
    public bool InUse;
    public bool IsDirectory;
    public bool HasFileName;
    public ulong ParentRecordNumber;
    public string Name = string.Empty;
    public bool HasPrimaryData;            // unnamed $DATA present
    public bool PrimaryDataResident;       // occupies the MFT record; no separate cluster allocation
    public ulong LogicalSize;              // from $DATA real/end-of-file
    public ulong DataAllocatedSize;        // from $DATA allocation (this record's instances)
    public ulong AdsAllocatedSize;         // named streams
    public ulong IndexAllocationSize;      // $INDEX_ALLOCATION (directories)
    public int FileNameLinks;              // FILE_NAME attributes seen (hardlink count)
    public int NameRank;                   // winning $FILE_NAME namespace rank (4 WIN32 .. 1 DOS, 0 none)
    public List<MftNameInfo>? NameLinks;    // allocated only when there is more than one real name
    public bool Incomplete;               // recoverable parser output must not become a complete scan
    public bool Compressed;
    public bool Sparse;
    public bool Reparse;                 // FILE_ATTRIBUTE_REPARSE_POINT seen in SI or FILE_NAME
    public uint ReparseTag;              // $REPARSE_POINT tag (0 when absent)
    public bool Offline;                 // OFFLINE or RECALL_ON_* seen in SI or FILE_NAME (cloud placeholder)

    public MftEntryInfo() { }
}

/// <summary>
/// Parses raw NTFS MFT file record segments: applies the update-sequence fixup and walks
/// attributes ($FILE_NAME, $DATA incl. ADS, $INDEX_ALLOCATION). Pure function over memory —
/// fully unit-testable without a volume.
/// </summary>
public static unsafe class NtfsRecordParser
{
    private const uint AttrStandardInformation = 0x00000010;
    private const uint AttrAttributeList = 0x00000020;
    private const uint AttrFileName = 0x00000030;
    private const uint AttrData = 0x00000080;
    private const uint AttrIndexAllocation = 0x000000A0;
    private const uint AttrReparsePoint = 0x000000C0;
    private const uint AttributeEndMarker = 0xFFFFFFFF;

    public static bool LooksLikeFileRecord(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= 4 && buffer[0] == (byte)'F' && buffer[1] == (byte)'I'
            && buffer[2] == (byte)'L' && buffer[3] == (byte)'E';

    public const byte FailNone = 0;
    public const byte FailMagic = 1;
    public const byte FailFixup = 2;
    public const byte FailStructure = 3;

    /// <summary>
    /// Locates the unnamed non-resident $DATA attribute of a record and decodes its
    /// cluster-run list (used for $MFT itself so multi-extent MFTs can be read correctly).
    /// Returns false when no suitable $DATA exists.
    /// </summary>
    public static unsafe bool TryDecodeDataRuns(
        byte* rec, int recordLength, List<(long StartLcn, ulong Clusters)> extents)
    {
        if (recordLength < 0x30) return false;
        try
        {
            var record = new Span<byte>(rec, recordLength);
            MftExtentReader.ApplyFixup(record);
            uint used = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]);
            int offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
            if (used > recordLength || offset < 0x30 || offset > used) return false;
            while (offset <= used - 4)
            {
                uint type = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
                if (type == AttributeEndMarker) break;
                if (used - offset < 0x10) return false;
                uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
                if (length < 0x18 || length > used - offset || (length & 7) != 0) return false;
                var attribute = record.Slice(offset, (int)length);
                if (type == AttrData && attribute[8] != 0 && attribute[9] == 0)
                {
                    var decoded = MftExtentReader.DecodeRuns(attribute, long.MaxValue);
                    if (decoded[0].Vcn != 0) return false;
                    foreach (var extent in decoded) extents.Add((extent.Lcn, extent.Clusters));
                    return true;
                }
                offset += (int)length;
            }
        }
        catch (IOException) { }
        catch (OverflowException) { }
        return false;
    }

    /// <summary>Parses one record in place. Returns false when magic/fixup validation fails;
    /// <paramref name="failStage"/> reports where.</summary>
    public static bool TryParseRecord(byte* rec, int recordLength, out MftEntryInfo info)
        => TryParseRecord(rec, recordLength, out info, out _);

    public static bool TryParseRecord(byte* rec, int recordLength, out MftEntryInfo info, out byte failStage)
    {
        info = new MftEntryInfo();
        failStage = FailMagic;

        if (recordLength < 0x30) return false;
        if (!LooksLikeFileRecord(new ReadOnlySpan<byte>(rec, 4))) return false;

        ushort usaOffset = *(ushort*)(rec + 0x04);
        ushort usaCount = *(ushort*)(rec + 0x06);
        failStage = FailFixup;
        if (usaCount < 2) return false;

        // Update-sequence fixup: the last 2 bytes of every 512-byte sector are stale and
        // must equal the sequence word before being replaced by the corrector values.
        int stride = 512;
        if (usaCount > 1)
        {
            int s = recordLength / (usaCount - 1);
            if (s >= 512 && s <= recordLength && (s & (s - 1)) == 0)
                stride = s;
        }
        if (usaCount >= 1 && usaOffset >= 0x30 && usaOffset + usaCount * 2U <= (uint)recordLength)
        {
            ushort sequence = *(ushort*)(rec + usaOffset);
            for (int i = 1; i < usaCount; i++)
            {
                int endOfSector = i * stride - 2;
                if (endOfSector + 2 > recordLength) { failStage = FailFixup; return false; }
                ushort* slot = (ushort*)(rec + endOfSector);
                if (*slot != sequence) { failStage = FailFixup; return false; } // torn/corrupt record
                *slot = *(ushort*)(rec + usaOffset + i * 2);
            }
        }
        else if (usaCount > 0)
        {
            failStage = FailFixup;
            return false; // nonsensical USA geometry
        }

        failStage = FailStructure;

        ushort attrsOffset = *(ushort*)(rec + 0x14);
        uint usedSize = *(uint*)(rec + 0x18);
        ushort recordFlags = *(ushort*)(rec + 0x16);
        // base-file-record packs record#:48 | sequence:16 — mask off the sequence bits
        // or lookups against record-number keys fail for reused records.
        ulong baseReference = *(ulong*)(rec + 0x20);
        long baseRecord = (long)(baseReference & 0x0000FFFFFFFFFFFFUL);
        info.RecordNumber = *(uint*)(rec + 0x2C);
        info.SequenceNumber = *(ushort*)(rec + 0x10);
        info.BaseRecordNumber = baseRecord;
        info.IsExtension = baseReference != 0;
        info.InUse = (recordFlags & 0x0001) != 0;
        info.IsDirectory = (recordFlags & 0x0002) != 0;

        uint limit = Math.Min(usedSize, (uint)recordLength);
        if (attrsOffset < 0x30 || attrsOffset > limit || usedSize > recordLength) return false;
        int bestNameRank = 0;
        bool foundEnd = false;

        uint off = attrsOffset;
        while (off <= limit && limit - off >= 4)
        {
            uint type = *(uint*)(rec + off);
            if (type == AttributeEndMarker) { foundEnd = true; break; }
            if (limit - off < 0x10) { info.Incomplete = true; break; }

            uint attrLen = *(uint*)(rec + off + 4);
            if (attrLen < 0x10 || attrLen > limit - off || (attrLen & 7) != 0)
            {
                if (!info.HasFileName) return false; // nothing usable: zero-byte-node fix
                info.Incomplete = true;
                break; // torn tail: keep what we parsed so far
            }

            bool nonResident = rec[off + 8] != 0;
            uint attrFlags = *(ushort*)(rec + off + 0x0C);
            if ((!nonResident && (attrLen < 0x18
                    || *(ushort*)(rec + off + 0x14) < 0x18
                    || (ulong)*(ushort*)(rec + off + 0x14) + *(uint*)(rec + off + 0x10) > attrLen))
                || (nonResident && attrLen < 0x40))
            {
                info.Incomplete = true;
                off += attrLen;
                continue;
            }

            switch (type)
            {
                case AttrStandardInformation:
                    ParseStandardInformation(rec, off, attrLen, ref info);
                    break;

                case AttrFileName:
                    ParseFileName(rec, off, attrLen, ref info, ref bestNameRank);
                    break;

                case AttrData:
                    ParseData(rec, off, attrLen, nonResident, attrFlags, ref info);
                    break;

                case AttrIndexAllocation when nonResident && attrLen >= 0x40:
                    // Size fields are valid only on the first extent record.
                    if (*(ulong*)(rec + off + 0x10) == 0)
                        info.IndexAllocationSize += *(ulong*)(rec + off + 0x28);
                    break;
                case AttrReparsePoint:
                    ParseReparsePoint(rec, off, attrLen, ref info);
                    break;

                case AttrAttributeList:
                    // Extension contents resolve via BaseRecordNumber fold in
                    // NtfsMftScanner.MergeExtensionRecords; nothing to record here.
                    break;
            }

            off += attrLen;
        }

        info.Incomplete |= !foundEnd;
        failStage = info.Incomplete ? FailStructure : FailNone;
        return true;
    }

    private static void ParseFileName(byte* rec, uint off, uint attrLen, ref MftEntryInfo info, ref int bestNameRank)
    {
        if (nonResidentCheck(rec, off)) return; // FILE_NAME is always resident
        uint valueOffset = *(ushort*)(rec + off + 0x14);
        uint valueLength = *(uint*)(rec + off + 0x10);
        if ((ulong)valueOffset + valueLength > attrLen || valueLength < 0x42) { info.Incomplete = true; return; }

        byte* v = rec + off + valueOffset;
        byte nameLen = v[0x40];
        byte ns = v[0x41];
        if (nameLen == 0 || valueLength < 0x42 + (uint)nameLen * 2 || ns > 3) { info.Incomplete = true; return; }

        if (ns != 2) info.FileNameLinks++;

        // Flags DWORD at value+0x38 (allocated/real sizes precede it; 0x42 minimum
        // length already validated above, so this read is in bounds).
        ApplyAttributeFlags(*(uint*)(v + 0x38), ref info);

        // Prefer WIN32 names over DOS-mangled ones; POSIX/BOTH acceptable.
        // DOS (ns==2) keeps rank 1 so it wins only when nothing better was seen.
        int rank = ns switch { 1 => 4, 3 => 3, 0 => 2, _ => 1 };
        ulong parent = *(ulong*)v & 0x0000FFFFFFFFFFFF;
        string? name = null;
        if (ns != 2 && info.HasFileName && bestNameRank > 1)
        {
            name = new string((char*)(v + 0x42), 0, nameLen);
            AddNameLink(ref info, new MftNameInfo(parent, name));
        }
        if (rank <= bestNameRank && info.HasFileName) return;

        info.ParentRecordNumber = parent;
        info.Name = name ?? new string((char*)(v + 0x42), 0, nameLen);
        info.HasFileName = true;
        info.NameRank = rank;
        bestNameRank = rank;
    }

    internal static void AddNameLink(ref MftEntryInfo info, MftNameInfo link)
    {
        if (!info.HasFileName || info.NameRank <= 1) return;
        var first = new MftNameInfo(info.ParentRecordNumber, info.Name);
        if (info.NameLinks is null)
        {
            if (first == link) return;
            info.NameLinks = new List<MftNameInfo>(2) { first, link };
        }
        else if (!info.NameLinks.Contains(link)) info.NameLinks.Add(link);
    }

    private static void ParseStandardInformation(byte* rec, uint off, uint attrLen, ref MftEntryInfo info)
    {
        if (nonResidentCheck(rec, off)) return; // STANDARD_INFORMATION is always resident
        uint valueOffset = *(ushort*)(rec + off + 0x14);
        uint valueLength = *(uint*)(rec + off + 0x10);
        if ((ulong)valueOffset + valueLength > attrLen || valueLength < 0x24) { info.Incomplete = true; return; }
        ApplyAttributeFlags(*(uint*)(rec + off + valueOffset + 0x20), ref info);
    }

    private static void ApplyAttributeFlags(uint attrs, ref MftEntryInfo info)
    {
        const uint REPARSE = 0x400;
        const uint OFFLINE = 0x1000;
        const uint RECALL_ON_OPEN = 0x40000;
        const uint RECALL_ON_DATA_ACCESS = 0x400000;
        if ((attrs & REPARSE) != 0) info.Reparse = true;
        if ((attrs & (OFFLINE | RECALL_ON_OPEN | RECALL_ON_DATA_ACCESS)) != 0) info.Offline = true;
    }

    private static void ParseData(byte* rec, uint off, uint attrLen, bool nonResident, uint attrFlags, ref MftEntryInfo info)
    {
        bool named = rec[off + 9] != 0; // name length byte

        if (!nonResident)
        {
            if ((ulong)*(ushort*)(rec + off + 0x14) + *(uint*)(rec + off + 0x10) > attrLen) return;
            ulong valueLen = *(uint*)(rec + off + 0x10);
            if (named)
            {
                // Resident streams live inside the MFT record. The $MFT allocation is
                // counted separately, so attributing bytes here would double-count them.
                return;
            }
            info.HasPrimaryData = true;
            info.PrimaryDataResident = true;
            info.LogicalSize = Math.Max(info.LogicalSize, valueLen);
            return;
        }

        if (attrLen < 0x40) return;
        ulong lowestVcn = *(ulong*)(rec + off + 0x10);

        // AllocatedLength/FileSize/ValidDataLength are undefined on continuation
        // records. The lowest-VCN-zero record already describes the whole stream.
        if (lowestVcn != 0)
        {
            if (!named) info.HasPrimaryData = true;
            if ((attrFlags & 0x0001) != 0) info.Compressed = true;
            if ((attrFlags & 0x8000) != 0) info.Sparse = true;
            return;
        }

        // Compressed/sparse streams carry the on-disk allocation as CompressedSize
        // at +0x40 (header grows to 0x48); +0x28 is the uncompressed logical size.
        bool isCompressedOrSparse = (attrFlags & 0x8001) != 0;
        if (isCompressedOrSparse && attrLen < 0x48) { info.Incomplete = true; return; }
        ulong alloc = isCompressedOrSparse ? *(ulong*)(rec + off + 0x40) : *(ulong*)(rec + off + 0x28);
        ulong real = *(ulong*)(rec + off + 0x30);

        if (named)
        {
            info.AdsAllocatedSize += alloc;
            return;
        }

        info.HasPrimaryData = true;
        info.LogicalSize = Math.Max(info.LogicalSize, real); // duplicated per extent-run record
        info.DataAllocatedSize += alloc;

        if ((attrFlags & 0x0001) != 0) info.Compressed = true;
        if ((attrFlags & 0x8000) != 0) info.Sparse = true;
    }

    private static void ParseReparsePoint(byte* rec, uint off, uint attrLen, ref MftEntryInfo info)
    {
        if (nonResidentCheck(rec, off)) return; // $REPARSE_POINT is resident
        uint valueOffset = *(ushort*)(rec + off + 0x14);
        uint valueLength = *(uint*)(rec + off + 0x10);
        if ((ulong)valueOffset + valueLength > attrLen || valueLength < 4) return;
        uint tag = *(uint*)(rec + off + valueOffset);
        if (tag != 0 && info.ReparseTag == 0) info.ReparseTag = tag; // first non-zero wins
    }

    private static bool nonResidentCheck(byte* rec, uint off) => rec[off + 8] != 0;
}
