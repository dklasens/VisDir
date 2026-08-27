using System.Diagnostics.CodeAnalysis;

namespace VisDir.Core.Scanning;

/// <summary>Flat snapshot of one parsed MFT file record segment.</summary>
public struct MftEntryInfo
{
    public long RecordNumber;
    public long BaseRecordNumber;          // 0 for base records
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
    public bool Compressed;
    public bool Sparse;

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
        if (!LooksLikeFileRecord(new ReadOnlySpan<byte>(rec, 4))) return false;

        ushort usaOffset = *(ushort*)(rec + 0x04);
        ushort usaCount = *(ushort*)(rec + 0x06);
        if (usaCount >= 1 && usaOffset >= 0x30 && usaOffset + usaCount * 2U <= (uint)recordLength)
        {
            ushort sequence = *(ushort*)(rec + usaOffset);
            for (int i = 1; i < usaCount; i++)
            {
                int endOfSector = i * 512 - 2;
                if (endOfSector + 2 > recordLength) return false;
                ushort* slot = (ushort*)(rec + endOfSector);
                if (*slot != sequence) return false;
                *slot = *(ushort*)(rec + usaOffset + i * 2);
            }
        }

        ushort attrsOffset = *(ushort*)(rec + 0x14);
        uint usedSize = Math.Min(*(uint*)(rec + 0x18), (uint)recordLength);

        uint off = attrsOffset;
        while (off + 0x10 <= usedSize)
        {
            uint type = *(uint*)(rec + off);
            if (type == AttributeEndMarker) break;
            uint attrLen = *(uint*)(rec + off + 4);
            if (attrLen < 0x10 || off + attrLen > usedSize || (attrLen & 7) != 0) return false;

            if (type == AttrData && rec[off + 8] != 0 /* non-resident */ && rec[off + 9] == 0 /* unnamed */
                && attrLen >= 0x40)
            {
                uint runsOffset = *(ushort*)(rec + off + 0x20);
                if (runsOffset == 0 || off + runsOffset >= off + attrLen) return false;

                byte* p = rec + off + runsOffset;
                byte* end = rec + off + attrLen;
                long lcn = 0;
                while (p < end && *p != 0)
                {
                    byte header = *p++;
                    uint lenBytes = (uint)(header & 0x0F);
                    uint offBytes = (uint)(header >> 4);
                    if (lenBytes == 0 || lenBytes > 8 || offBytes > 8 || p + lenBytes + offBytes > end) return false;

                    ulong clusters = 0;
                    for (uint i = 0; i < lenBytes; i++) clusters |= (ulong)*p++ << (int)(8 * i);

                    long delta = 0;
                    for (uint i = 0; i < offBytes; i++) delta |= (long)*p++ << (int)(8 * i);
                    int shift = (int)(8 * offBytes) - 1;
                    if ((delta >> shift) != 0) delta |= ~(long)0UL << (int)(8 * offBytes); // sign-extend

                    lcn += delta;
                    if (lcn < 0 || clusters == 0) return false;
                    extents.Add((lcn, clusters));
                }
                return extents.Count > 0;
            }

            off += attrLen;
        }
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

        // Update-sequence fixup: the last 2 bytes of every 512-byte sector are stale and
        // must equal the sequence word before being replaced by the corrector values.
        if (usaCount >= 1 && usaOffset >= 0x30 && usaOffset + usaCount * 2U <= (uint)recordLength)
        {
            ushort sequence = *(ushort*)(rec + usaOffset);
            for (int i = 1; i < usaCount; i++)
            {
                int endOfSector = i * 512 - 2;
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
        long baseRecord = (long)(*(ulong*)(rec + 0x20) & 0x0000FFFFFFFFFFFFUL);
        info.RecordNumber = *(uint*)(rec + 0x2C);
        info.BaseRecordNumber = baseRecord;
        info.InUse = (recordFlags & 0x0001) != 0;
        info.IsDirectory = (recordFlags & 0x0002) != 0;

        uint limit = Math.Min(usedSize, (uint)recordLength);
        int bestNameRank = 0;

        uint off = attrsOffset;
        while (off + 0x10 <= limit)
        {
            uint type = *(uint*)(rec + off);
            if (type == AttributeEndMarker) break;

            uint attrLen = *(uint*)(rec + off + 4);
            if (attrLen < 0x10 || off + attrLen > limit || (attrLen & 7) != 0)
                break; // truncated/torn tail: keep what we parsed so far

            bool nonResident = rec[off + 8] != 0;
            uint attrFlags = *(ushort*)(rec + off + 0x0C);

            switch (type)
            {
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
            }

            off += attrLen;
        }

        return true;
    }

    private static void ParseFileName(byte* rec, uint off, uint attrLen, ref MftEntryInfo info, ref int bestNameRank)
    {
        if (nonResidentCheck(rec, off)) return; // FILE_NAME is always resident
        uint valueOffset = *(ushort*)(rec + off + 0x14);
        uint valueLength = *(uint*)(rec + off + 0x10);
        if (valueOffset + valueLength > attrLen || valueLength < 0x42) return;

        byte* v = rec + off + valueOffset;
        info.ParentRecordNumber = *(ulong*)v & 0x0000FFFFFFFFFFFF;

        byte nameLen = v[0x40];
        byte ns = v[0x41];
        if (nameLen == 0 || valueLength < 0x42 + (uint)nameLen * 2) return;

        info.FileNameLinks++;

        // Prefer WIN32 names over DOS-mangled ones; POSIX/BOTH acceptable.
        int rank = ns switch { 1 => 4, 3 => 3, 0 => 2, _ => 1 };
        if (rank <= bestNameRank && info.HasFileName) return;

        info.Name = new string((char*)(v + 0x42), 0, nameLen);
        info.HasFileName = true;
        bestNameRank = rank;
    }

    private static void ParseData(byte* rec, uint off, uint attrLen, bool nonResident, uint attrFlags, ref MftEntryInfo info)
    {
        bool named = rec[off + 9] != 0; // name length byte

        if (!nonResident)
        {
            if (*(ushort*)(rec + off + 0x14) + *(uint*)(rec + off + 0x10) > attrLen) return;
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

        ulong alloc = *(ulong*)(rec + off + 0x28);
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

    private static bool nonResidentCheck(byte* rec, uint off) => rec[off + 8] != 0;
}
