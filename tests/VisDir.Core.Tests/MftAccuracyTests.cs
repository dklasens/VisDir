using System.Buffers.Binary;
using System.Reflection;
using VisDir.Core;
using VisDir.Core.Interop;
using VisDir.Core.Scanning;

namespace VisDir.Core.Tests;

public class MftAccuracyTests
{
    [Fact]
    public void DirectoryStreamsAreIncludedAndSeparatelyAccounted()
    {
        var node = new FsNode();
        NtfsMftScanner.ApplyInfo(node, new MftEntryInfo
        {
            IsDirectory = true, IndexAllocationSize = 4096, AdsAllocatedSize = 1_048_576,
        }, 4096);
        Assert.Equal(1_052_672UL, node.AllocatedSize);
        Assert.Equal(1_048_576UL, node.StreamAllocatedSize);
        Assert.Equal(4096UL, node.MetadataAllocatedSize);
    }

    [Fact]
    public unsafe void RealHardlinkNamesSurviveParserAndGetSharedPathAttribution()
    {
        byte[] record = Record(42, false, FileName(20, "z.bin", 1), FileName(10, "a.bin", 1), FileName(20, "Z~1.BIN", 2));
        MftEntryInfo info;
        fixed (byte* p = record) Assert.True(NtfsRecordParser.TryParseRecord(p, record.Length, out info));
        Assert.Equal(2, info.NameLinks!.Count);
        info.DataAllocatedSize = 4096;
        var entries = TreeEntries();
        entries[42] = info;
        FsNode root = NtfsMftScanner.BuildTree(entries, Capacity);
        HardlinkAccounting.Apply(root);
        TreeOps.Finalize(root);
        FsNode a = root.Children!.Single(n => n.Name == "A");
        FsNode z = root.Children!.Single(n => n.Name == "Z");
        Assert.Equal("a.bin", Assert.Single(a.Children!).Name);
        Assert.Equal("z.bin", Assert.Single(z.Children!).Name);
        Assert.Equal(4096UL, a.TotalAllocated);
        Assert.Equal(0UL, z.TotalAllocated);
        Assert.Equal(4096UL, root.TotalAllocated);
        Assert.Equal(unchecked((long)((1UL << 48) | 42)), a.Children![0].FileKey);
    }

    [Fact]
    public void SubtreeOwnsHardlinkWhenAnotherAliasIsOutsideScope()
    {
        var entries = TreeEntries();
        entries[42] = new MftEntryInfo
        {
            RecordNumber = 42, SequenceNumber = 1, HasFileName = true, NameRank = 4,
            Name = "a.bin", ParentRecordNumber = 10, FileNameLinks = 2, DataAllocatedSize = 4096,
            NameLinks = new() { new(10, "a.bin"), new(20, "z.bin") },
        };
        FsNode root = NtfsMftScanner.BuildTree(entries, Capacity);
        FsNode subtree = root.Children!.Single(n => n.Name == "Z");
        subtree.Parent = null;
        subtree.Name = "C:\\Z";
        HardlinkAccounting.Apply(subtree);
        TreeOps.Finalize(subtree);
        Assert.Equal(4096UL, subtree.TotalAllocated);
    }

    [Fact]
    public void MftAccountingMarksEveryAliasWithoutIndexingUniqueFiles()
    {
        var entries = TreeEntries();
        entries[42] = new MftEntryInfo
        {
            RecordNumber = 42, HasFileName = true, NameRank = 4, Name = "z.bin", ParentRecordNumber = 20,
            DataAllocatedSize = 4096, NameLinks = new() { new(10, "a.bin"), new(20, "z.bin") },
        };
        entries[43] = new MftEntryInfo
        {
            RecordNumber = 43, HasFileName = true, Name = "unique", ParentRecordNumber = 5, DataAllocatedSize = 8192,
        };
        FsNode root = NtfsMftScanner.BuildTree(entries, Capacity);
        Assert.Equal(4096UL, HardlinkAccounting.Apply(root, onlyMarkedHardlinks: true));
        TreeOps.Finalize(root);
        Assert.Equal(12_288UL, root.TotalAllocated);
        Assert.Equal(4096UL, root.Children!.Single(n => n.Name == "A").TotalAllocated);
        Assert.Equal(0UL, root.Children!.Single(n => n.Name == "Z").TotalAllocated);
    }

    [Fact]
    public unsafe void TornTailRetainsUsefulNameButIsExplicitlyIncomplete()
    {
        byte[] malformed = new byte[16];
        Put(malformed, 0, 0x80u);
        Put(malformed, 4, 0xfffffff8u);
        byte[] record = Record(42, false, FileName(5, "useful", 1), malformed);
        fixed (byte* p = record)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, record.Length, out MftEntryInfo info));
            Assert.Equal("useful", info.Name);
            Assert.True(info.Incomplete);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public unsafe void MissingSectorFixupsAreRejected(ushort usaCount)
    {
        byte[] record = Record(42, false, FileName(5, "file", 1));
        Put(record, 6, usaCount);
        fixed (byte* p = record)
        {
            Assert.False(NtfsRecordParser.TryParseRecord(p, record.Length, out _, out byte stage));
            Assert.Equal(NtfsRecordParser.FailFixup, stage);
        }
    }

    [Fact]
    public void DiscoversFragmentedMftThroughResidentAttributeList()
    {
        byte[] image = FragmentedImage(nonResidentList: false);
        var runs = Discover(image);
        Assert.Equal(new[] { (1L, 4UL), (10L, 1UL) }, runs);
    }

    [Fact]
    public void DiscoversFragmentedMftThroughNonResidentAttributeList()
    {
        byte[] image = FragmentedImage(nonResidentList: true);
        var runs = Discover(image);
        Assert.Equal(new[] { (1L, 4UL), (10L, 1UL) }, runs);
    }

    [Fact]
    public void IncompleteRunCoverageFailsInsteadOfGuessingContiguousLayout()
    {
        byte[] image = new byte[16 * 4096];
        Record(0, false, DataRuns(0, 4, 1, 20 * 1024)).CopyTo(image, 4096);
        Assert.Throws<IOException>(() => Discover(image));
    }

    [Fact]
    public void StaleExtensionReferenceFailsClosed()
    {
        byte[] image = FragmentedImage(nonResidentList: false);
        // The extension record still has a valid sector fixup but the wrong sequence.
        Put(image, 4096 + 15 * 1024 + 0x10, (ushort)3);
        Assert.Throws<IOException>(() => Discover(image));
    }

    [Fact]
    public void InvalidRunOutsideVolumeFailsClosed()
    {
        byte[] image = new byte[16 * 4096];
        Record(0, false, DataRuns(0, 5, 15, 20 * 1024)).CopyTo(image, 4096);
        Assert.Throws<IOException>(() => Discover(image));
    }

    [Fact]
    public void DeepCyclesDoNotDisappearFromTree()
    {
        var entries = TreeEntries();
        for (int i = 100; i < 200; i++)
            entries[i] = new MftEntryInfo
            {
                RecordNumber = i, HasFileName = true, Name = $"cycle{i}", IsDirectory = true,
                ParentRecordNumber = (ulong)(i == 199 ? 100 : i + 1),
            };
        FsNode root = NtfsMftScanner.BuildTree(entries, Capacity);
        TreeOps.Finalize(root);
        FsNode orphans = root.Children!.Single(n => (n.Flags & NodeFlags.OrphanedRoot) != 0);
        Assert.Equal(100, orphans.Children!.Count);
    }

    [Fact]
    public void TreeBuildHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => NtfsMftScanner.BuildTree(TreeEntries(), Capacity, cancellation.Token));
    }

    [Fact]
    public void MountedVolumeIdentityCannotScanHostDriveByMistake()
    {
        NtfsMftScanner.ValidateVolumeIdentity(0x1234_0000_5678, 0x1234_0000_5678);
        Assert.Throws<IOException>(() => NtfsMftScanner.ValidateVolumeIdentity(0x1234_0000_5678, 0x9999_0000_5678));
    }

    [Fact]
    public void ExtensionOfMftRecordZeroIsFolded()
    {
        var entries = new Dictionary<long, MftEntryInfo>
        {
            [0] = new() { RecordNumber = 0, DataAllocatedSize = 8192 },
            [15] = new() { RecordNumber = 15, BaseRecordNumber = 0, IsExtension = true, AdsAllocatedSize = 4096 },
        };
        NtfsMftScanner.MergeExtensionRecords(entries, new() { 15 });
        Assert.Single(entries);
        Assert.Equal(4096UL, entries[0].AdsAllocatedSize);
    }

    [Fact]
    public void AsyncMftReadRejectsUnexpectedShortRead()
    {
        WithMftFile(16, (folder, volume) =>
        {
            volume.MftValidDataLength = 20 * 1024;
            var error = Assert.Throws<TargetInvocationException>(() => ReadMft(folder, volume));
            Assert.IsAssignableFrom<IOException>(error.InnerException);
        });
    }

    [Fact]
    public void AsyncMftReadReturnsEveryRecord()
    {
        WithMftFile(20, (folder, volume) =>
        {
            Dictionary<long, MftEntryInfo> result = ReadMft(folder, volume);
            Assert.Equal(20, result.Count);
            Assert.Contains(19L, result.Keys);
        });
    }

    [Fact]
    public void AsyncMftReadRejectsIncompleteInUseRecord()
    {
        WithMftFile(20, (folder, volume) =>
        {
            byte[] tail = new byte[16];
            Put(tail, 0, 0x80u); Put(tail, 4, 0xfffffff8u);
            using (var file = new FileStream(Path.Combine(folder, "$MFT"), FileMode.Open, FileAccess.Write))
            {
                file.Position = 7 * 1024;
                file.Write(Record(7, false, FileName(5, "broken", 1), tail));
            }
            var error = Assert.Throws<TargetInvocationException>(() => ReadMft(folder, volume));
            Assert.IsAssignableFrom<IOException>(error.InnerException);
        });
    }

    [Fact]
    public void PipelinedReadCanCancelAfterFirstChunk()
    {
        WithMftFile(6, (folder, volume) =>
        {
            using (var file = new FileStream(Path.Combine(folder, "$MFT"), FileMode.Open, FileAccess.Write))
            {
                file.SetLength((32 << 20) + 1024);
                file.Position = 32 << 20;
                file.Write(Record(32768, false));
            }
            volume.MftValidDataLength = (32 << 20) + 1024;
            volume.TotalClusters = 20_000;
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress(_ => cancellation.Cancel());
            var error = Assert.Throws<TargetInvocationException>(() => ReadMft(folder, volume, cancellation.Token, progress));
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        });
    }

    [Fact]
    public void ExtensionPreservesAllNamesWhenBaseHasNoName()
    {
        var entries = new Dictionary<long, MftEntryInfo>
        {
            [42] = new() { RecordNumber = 42 },
            [43] = new()
            {
                RecordNumber = 43, BaseRecordNumber = 42, HasFileName = true, NameRank = 4,
                Name = "first", ParentRecordNumber = 10,
                NameLinks = new() { new(10, "first"), new(20, "second") },
            },
        };
        NtfsMftScanner.MergeExtensionRecords(entries, new() { 43 });
        Assert.Equal(2, entries[42].NameLinks!.Count);
    }

    private static readonly VolumeInfo Capacity = new() { RootPath = "C:\\", DisplayName = "Test", VolumeSerialNumber = 123 };

    private static Dictionary<long, MftEntryInfo> TreeEntries() => new()
    {
        [5] = new() { RecordNumber = 5, IsDirectory = true, HasFileName = true, Name = "C:\\", ParentRecordNumber = 5 },
        [10] = new() { RecordNumber = 10, IsDirectory = true, HasFileName = true, Name = "A", ParentRecordNumber = 5 },
        [20] = new() { RecordNumber = 20, IsDirectory = true, HasFileName = true, Name = "Z", ParentRecordNumber = 5 },
    };

    private static List<(long StartLcn, ulong Clusters)> Discover(byte[] image) => MftExtentReader.Discover(
        (offset, count) => image.AsSpan(checked((int)offset), count).ToArray(),
        4096, 1024, 1, (ulong)image.Length / 4096, 20 * 1024, CancellationToken.None);

    private static byte[] FragmentedImage(bool nonResidentList)
    {
        byte[] image = new byte[16 * 4096];
        byte[] list = new byte[64];
        WriteReference(list, 0, 0, 1UL << 48);
        WriteReference(list, 32, 4, (2UL << 48) | 15);
        byte[] attributeList;
        if (nonResidentList)
        {
            attributeList = DataRuns(0, 1, 12, 64);
            Put(attributeList, 0, 0x20u);
            list.CopyTo(image, 12 * 4096);
        }
        else attributeList = Resident(0x20, list);
        Record(0, false, DataRuns(0, 4, 1, 20 * 1024), attributeList).CopyTo(image, 4096);
        byte[] extension = Record(15, false, DataRuns(4, 1, 10, 20 * 1024));
        Put(extension, 0x10, (ushort)2);
        Put(extension, 0x20, 1UL << 48);
        extension.CopyTo(image, 4096 + 15 * 1024);
        return image;
    }

    private static void WriteReference(byte[] list, int offset, ulong vcn, ulong record)
    {
        Put(list, offset, 0x80u);
        Put(list, offset + 4, (ushort)32);
        Put(list, offset + 8, vcn);
        Put(list, offset + 16, record);
        Put(list, offset + 24, (ushort)1);
    }

    private static byte[] DataRuns(ulong lowest, byte clusters, byte lcn, ulong length)
    {
        var attribute = new byte[0x48];
        Put(attribute, 0, 0x80u); Put(attribute, 4, (uint)attribute.Length);
        attribute[8] = 1; Put(attribute, 0x0e, (ushort)1);
        Put(attribute, 0x10, lowest); Put(attribute, 0x18, lowest + clusters - 1);
        Put(attribute, 0x20, (ushort)0x40); Put(attribute, 0x28, length); Put(attribute, 0x30, length);
        attribute[0x40] = 0x11; attribute[0x41] = clusters; attribute[0x42] = lcn;
        return attribute;
    }

    private static byte[] FileName(ulong parent, string name, byte ns)
    {
        byte[] value = new byte[0x42 + name.Length * 2];
        Put(value, 0, parent); value[0x40] = (byte)name.Length; value[0x41] = ns;
        System.Text.Encoding.Unicode.GetBytes(name).CopyTo(value, 0x42);
        return Resident(0x30, value);
    }

    private static byte[] Resident(uint type, byte[] value)
    {
        var attribute = new byte[(0x18 + value.Length + 7) & ~7];
        Put(attribute, 0, type); Put(attribute, 4, (uint)attribute.Length);
        Put(attribute, 0x10, (uint)value.Length); Put(attribute, 0x14, (ushort)0x18);
        value.CopyTo(attribute, 0x18);
        return attribute;
    }

    private static byte[] Record(uint number, bool directory, params byte[][] attributes)
    {
        var record = new byte[1024];
        "FILE"u8.CopyTo(record);
        Put(record, 4, (ushort)0x30); Put(record, 6, (ushort)3);
        Put(record, 0x10, (ushort)1); Put(record, 0x14, (ushort)0x38);
        Put(record, 0x16, directory ? (ushort)3 : (ushort)1); Put(record, 0x2c, number);
        int offset = 0x38;
        foreach (byte[] attribute in attributes) { attribute.CopyTo(record, offset); offset += attribute.Length; }
        Put(record, offset, 0xffffffffu); Put(record, 0x18, (uint)(offset + 8));
        Put(record, 0x30, (ushort)0xaaaa);
        for (int i = 1; i <= 2; i++)
        {
            Put(record, 0x30 + i * 2, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(i * 512 - 2)));
            Put(record, i * 512 - 2, (ushort)0xaaaa);
        }
        return record;
    }

    private static void Put<T>(byte[] bytes, int offset, T value) where T : unmanaged
        => System.Runtime.InteropServices.MemoryMarshal.Write(bytes.AsSpan(offset), in value);

    private static Dictionary<long, MftEntryInfo> ReadMft(string folder, NtfsNative.NtfsVolumeDataBuffer volume,
        CancellationToken ct = default, IProgress<ScanProgress>? progress = null)
        => (Dictionary<long, MftEntryInfo>)typeof(NtfsMftScanner).GetMethod("ReadMft", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { IntPtr.Zero, folder, volume, ct, progress })!;

    private sealed class InlineProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }

    private static void WithMftFile(int records, Action<string, NtfsNative.NtfsVolumeDataBuffer> action)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"VisDir-mft-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            using (var file = File.Create(Path.Combine(folder, "$MFT")))
                for (uint i = 0; i < records; i++) file.Write(Record(i, i == 5));
            action(folder, new NtfsNative.NtfsVolumeDataBuffer
            {
                BytesPerFileRecordSegment = 1024, BytesPerCluster = 4096, TotalClusters = 100,
                MftValidDataLength = (ulong)records * 1024,
            });
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
