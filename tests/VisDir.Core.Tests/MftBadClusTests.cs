using VisDir.Core.Scanning;
using Xunit;

namespace VisDir.Core.Tests;

public class MftBadClusTests
{
    private const ulong HugeAds = 2_000_000_000_000UL; // ~1.8 TiB, the $BadClus phantom scale

    [Fact]
    public void ZeroBadClus_ZeroesAllFourSizeFieldsOnRecord8()
    {
        var entries = new Dictionary<long, MftEntryInfo>
        {
            [8] = new MftEntryInfo
            {
                RecordNumber = 8,
                LogicalSize = HugeAds,
                DataAllocatedSize = HugeAds,
                AdsAllocatedSize = HugeAds,
                IndexAllocationSize = 12345,
            },
        };

        NtfsMftScanner.ZeroBadClus(entries);

        MftEntryInfo bad = entries[8];
        Assert.Equal(0UL, bad.LogicalSize);
        Assert.Equal(0UL, bad.DataAllocatedSize);
        Assert.Equal(0UL, bad.AdsAllocatedSize);
        Assert.Equal(0UL, bad.IndexAllocationSize);
    }

    [Fact]
    public void ZeroBadClus_LeavesNormalRecordsAndMissing8Untouched()
    {
        var entries = new Dictionary<long, MftEntryInfo>
        {
            [42] = new MftEntryInfo
            {
                RecordNumber = 42,
                LogicalSize = 100,
                DataAllocatedSize = 4096,
                AdsAllocatedSize = 8192,
                IndexAllocationSize = 4096,
            },
        };

        NtfsMftScanner.ZeroBadClus(entries);

        Assert.Single(entries);
        MftEntryInfo file = entries[42];
        Assert.Equal(100UL, file.LogicalSize);
        Assert.Equal(4096UL, file.DataAllocatedSize);
        Assert.Equal(8192UL, file.AdsAllocatedSize);
        Assert.Equal(4096UL, file.IndexAllocationSize);
    }

    [Fact]
    public unsafe void ParserTrap_NamedBadStreamBillsAdsWithoutSparseFlag()
    {
        // Exact $BadClus shape that sailed through the demoted Sparse||Compressed
        // guard: named non-resident $DATA at lowest VCN 0 with no 0x0001/0x8000
        // flags, so ParseData bills +0x28 into Ads and returns before setting Sparse.
        var b = new RecordBuilder(1024, usaCount: 3, recNo: 8);
        b.Append(NonResidentDataAttr(attrFlags: 0x0000, alloc: HugeAds, real: HugeAds, compressed: 0, lowestVcn: 0, name: "$Bad"));
        byte[] rec = b.Finish();

        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.Equal(HugeAds, info.AdsAllocatedSize);
            Assert.False(info.Sparse);
        }
    }

    [Fact]
    public void ZeroBadClus_PostFoldPlacementKeepsRecord8AtZero()
    {
        // Spill-segment resurrection shape: a zeroed rec-8 base plus an extension
        // still pointing at it with huge Ads. ZeroBadClus models the post-fold
        // position, so rec 8 stays billed at zero.
        var entries = new Dictionary<long, MftEntryInfo>
        {
            [8] = new MftEntryInfo { RecordNumber = 8 },
            [9999] = new MftEntryInfo { RecordNumber = 9999, BaseRecordNumber = 8, AdsAllocatedSize = HugeAds },
        };

        NtfsMftScanner.ZeroBadClus(entries);

        Assert.Equal(0UL, entries[8].AdsAllocatedSize);
        Assert.Equal(0UL, entries[8].LogicalSize);
        Assert.Equal(0UL, entries[8].DataAllocatedSize);
        Assert.Equal(0UL, entries[8].IndexAllocationSize);
    }

    // Minimal synthetic FILE-record builders, duplicated locally because the
    // CoreTests.cs copies are private to that file (which this change cannot touch).
    private const ushort UsaOffset = 0x30;
    private const ushort UsaSequence = 0xA55A;

    private sealed class RecordBuilder
    {
        public byte[] Buf;
        public int Cursor;
        public ushort UsaCount;

        public RecordBuilder(int size, ushort usaCount, ushort flags = 0x0001, uint recNo = 7, ulong baseRef = 0)
        {
            Buf = new byte[size];
            UsaCount = usaCount;
            "FILE"u8.CopyTo(Buf);
            BitConverter.GetBytes(UsaOffset).CopyTo(Buf, 4);
            BitConverter.GetBytes(usaCount).CopyTo(Buf, 6);
            int attrsOff = (UsaOffset + usaCount * 2 + 7) & ~7;
            BitConverter.GetBytes((ushort)attrsOff).CopyTo(Buf, 0x14);
            BitConverter.GetBytes(flags).CopyTo(Buf, 0x16);
            BitConverter.GetBytes(baseRef).CopyTo(Buf, 0x20);
            BitConverter.GetBytes(recNo).CopyTo(Buf, 0x2C);
            Cursor = attrsOff;
        }

        public void Append(byte[] attr)
        {
            attr.CopyTo(Buf, Cursor);
            Cursor += attr.Length;
        }

        public byte[] Finish()
        {
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(Buf, Cursor);
            Cursor += 8;
            BitConverter.GetBytes((uint)Cursor).CopyTo(Buf, 0x18);
            int sectors = UsaCount - 1;
            int stride = Buf.Length / sectors;
            for (int s = 1; s <= sectors; s++)
            {
                int slot = s * stride - 2;
                Buf[UsaOffset + s * 2] = Buf[slot];
                Buf[UsaOffset + s * 2 + 1] = Buf[slot + 1];
                Buf[slot] = (byte)(UsaSequence & 0xFF);
                Buf[slot + 1] = (byte)(UsaSequence >> 8);
            }
            BitConverter.GetBytes(UsaSequence).CopyTo(Buf, UsaOffset);
            return Buf;
        }
    }

    private static byte[] NonResidentDataAttr(ushort attrFlags, ulong alloc, ulong real, ulong compressed, ulong lowestVcn = 0, string? name = null)
    {
        int nameLen = name?.Length ?? 0;
        const int headerLen = 0x48;
        int nameOffset = headerLen;
        int runsOffset = headerLen + nameLen * 2;
        int attrLen = (runsOffset + 1 + 7) & ~7; // +1 zero run terminator
        var attr = new byte[attrLen];
        BitConverter.GetBytes(0x80u).CopyTo(attr, 0);
        BitConverter.GetBytes((uint)attrLen).CopyTo(attr, 4);
        attr[8] = 1; // non-resident
        attr[9] = (byte)nameLen;
        BitConverter.GetBytes((ushort)nameOffset).CopyTo(attr, 0x0A);
        BitConverter.GetBytes(attrFlags).CopyTo(attr, 0x0C);
        BitConverter.GetBytes(lowestVcn).CopyTo(attr, 0x10);
        BitConverter.GetBytes(lowestVcn).CopyTo(attr, 0x18); // highestVcn (unused by parser)
        BitConverter.GetBytes((ushort)runsOffset).CopyTo(attr, 0x20);
        BitConverter.GetBytes(alloc).CopyTo(attr, 0x28);
        BitConverter.GetBytes(real).CopyTo(attr, 0x30);
        BitConverter.GetBytes(real).CopyTo(attr, 0x38); // initialized size (unused by parser)
        BitConverter.GetBytes(compressed).CopyTo(attr, 0x40);
        if (name is not null)
            for (int i = 0; i < name.Length; i++)
                BitConverter.GetBytes(name[i]).CopyTo(attr, nameOffset + i * 2);
        attr[runsOffset] = 0;
        return attr;
    }
}
