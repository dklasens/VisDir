using VisDir.Core;
using VisDir.Core.Scanning;
using Xunit;

namespace VisDir.Core.Tests;

/// <summary>v1.2.4: recall-marked but resident files bill their full allocation
/// (live case: game payloads carrying RECALL_ON_* with fully allocated runs).
/// Zeroing them hid 189 GB; dehydrated placeholders still report 0 either way.</summary>
public class MftRecallTests
{
    private const ulong FifteenGiB = 15UL * 1024 * 1024 * 1024;

    [Fact]
    public void MftApplyInfo_RecallMarkedResident_BillsFullAndFlags()
    {
        var node = new FsNode { Name = "pak_compatmpcp.xpak" };
        var info = new MftEntryInfo
        {
            HasPrimaryData = true,
            LogicalSize = FifteenGiB,
            DataAllocatedSize = FifteenGiB,
            Offline = true,
        };
        NtfsMftScanner.ApplyInfo(node, info, 4096);
        Assert.Equal(FifteenGiB, node.AllocatedSize);
        Assert.Equal(FifteenGiB, node.LogicalSize);
        Assert.True((node.Flags & NodeFlags.CloudPlaceholder) != 0);
    }

    [Fact]
    public void GenericApplyAttributes_RecallMarked_BillsFullAndFlags()
    {
        var node = new FsNode { Name = "pak_compatmpcp.xpak", AllocatedSize = FifteenGiB };
        bool isDir = GenericScanner.ApplyAttributes(node, 0x40020, 0);
        Assert.False(isDir);
        Assert.Equal(FifteenGiB, node.AllocatedSize);
        Assert.True((node.Flags & NodeFlags.CloudPlaceholder) != 0);
    }

    [Fact]
    public void MergeExtensionRecords_DosBaseAdoptsWin32ExtensionName()
    {
        var entries = new Dictionary<long, MftEntryInfo>
        {
            [100] = new MftEntryInfo
            {
                RecordNumber = 100, InUse = true, HasFileName = true,
                Name = "CALLOF~1", NameRank = 1, ParentRecordNumber = 7,
            },
            [101] = new MftEntryInfo
            {
                RecordNumber = 101, BaseRecordNumber = 100, InUse = true,
                HasFileName = true, Name = "Call of Duty Modern Warfare 2019",
                NameRank = 4, ParentRecordNumber = 7,
                LogicalSize = 5, DataAllocatedSize = 6,
            },
        };
        NtfsMftScanner.MergeExtensionRecords(entries, new List<long> { 101 });
        MftEntryInfo folded = entries[100];
        Assert.Equal("Call of Duty Modern Warfare 2019", folded.Name);
        Assert.Equal(4, folded.NameRank);
        Assert.Equal(7UL, folded.ParentRecordNumber);
        Assert.Equal(5UL, folded.LogicalSize);
        Assert.Equal(6UL, folded.DataAllocatedSize);
        Assert.False(entries.ContainsKey(101));
    }

    [Fact]
    public void MergeExtensionRecords_Win32BaseKeepsNameAgainstDosExtension()
    {
        var entries = new Dictionary<long, MftEntryInfo>
        {
            [100] = new MftEntryInfo
            {
                RecordNumber = 100, InUse = true, HasFileName = true,
                Name = "pak_compatmpcp.xpak", NameRank = 4, ParentRecordNumber = 7,
            },
            [101] = new MftEntryInfo
            {
                RecordNumber = 101, BaseRecordNumber = 100, InUse = true,
                HasFileName = true, Name = "PAK_CO~2.XPA",
                NameRank = 1, ParentRecordNumber = 7,
            },
        };
        NtfsMftScanner.MergeExtensionRecords(entries, new List<long> { 101 });
        MftEntryInfo folded = entries[100];
        Assert.Equal("pak_compatmpcp.xpak", folded.Name);
        Assert.Equal(4, folded.NameRank);
        Assert.False(entries.ContainsKey(101));
    }
}
