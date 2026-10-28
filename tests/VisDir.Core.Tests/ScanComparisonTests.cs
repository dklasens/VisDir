using VisDir.Scanner;

namespace VisDir.Core.Tests;

public sealed class ScanComparisonTests
{
    [Theory]
    [InlineData(200_000_000UL, 100_000_000UL)]
    [InlineData(100_000_000UL, 0UL)]
    [InlineData(0UL, 100_000_000UL)]
    public void DifferentAllocation_FailsInBothDirectionsIncludingZero(ulong mft, ulong generic)
    {
        Assert.Equal(ComparisonStatus.Failed, ScanComparison.Compare(
            Result("mft", File("data.bin", mft)), Result("generic", File("data.bin", generic))).Status);
    }

    [Fact]
    public void EqualRootTotals_DoNotHideOffsettingPerPathErrors()
    {
        Assert.Equal(ComparisonStatus.Failed, ScanComparison.Compare(
            Result("mft", File("a", 200), File("b", 100)),
            Result("generic", File("a", 100), File("b", 200))).Status);
    }

    [Fact]
    public void ChecksEveryPathIncludingSmallFilesAfterFirstThirty()
    {
        FsNode[] left = Enumerable.Range(0, 40).Select(i => File("file" + i, 1000)).ToArray();
        FsNode[] right = Enumerable.Range(0, 40).Select(i => File("file" + i, i == 39 ? 1UL : 1000UL)).ToArray();
        Assert.Equal(ComparisonStatus.Failed, ScanComparison.Compare(Result("mft", left), Result("generic", right)).Status);
    }

    [Fact]
    public void MissingZeroByteFile_IsStillAPathMismatch()
    {
        Assert.Equal(ComparisonStatus.Failed, ScanComparison.Compare(Result("mft", File("empty", 0)), Result("generic")).Status);
        Assert.Equal(ComparisonStatus.Failed, ScanComparison.Compare(Result("mft"), Result("generic", File("empty", 0))).Status);
    }

    [Fact]
    public void AllowsOnlyMeasuredStreamsAndDirectoryMetadata()
    {
        FsNode file = File("data.bin", 300);
        file.StreamAllocatedSize = 200;
        var folder = new FsNode { Name = "folder", Flags = NodeFlags.Directory, AllocatedSize = 4096, MetadataAllocatedSize = 4096 };
        folder.AddChild(file);
        var peer = new FsNode { Name = "folder", Flags = NodeFlags.Directory };
        peer.AddChild(File("data.bin", 100));
        ComparisonResult result = ScanComparison.Compare(Result("mft", folder), Result("generic", peer));
        Assert.Equal(ComparisonStatus.Passed, result.Status);
        Assert.Equal(4296m, result.ExcludedBytes);
        Assert.Equal(100m, result.PrimaryBytes);
    }

    [Fact]
    public void AllowsKnownNtfsMetadataRecordsAndTheirSubtrees()
    {
        var extend = new FsNode { Name = "$Extend", Flags = NodeFlags.Directory, FileKey = 11 };
        extend.AddChild(File("$UsnJrnl", 10_000));
        var mft = File("$MFT", 20_000);
        mft.FileKey = 0;
        ComparisonResult result = ScanComparison.Compare(Result("mft", mft, extend, File("ordinary", 100)), Result("generic", File("ordinary", 100)));
        Assert.Equal(ComparisonStatus.Passed, result.Status);
        Assert.Equal(30_000m, result.ExcludedBytes);
    }

    [Fact]
    public void DollarPrefixedUserFile_IsNotSystemMetadataAllowance()
    {
        FsNode pretender = File("$MFT", 100);
        pretender.FileKey = 1200;
        Assert.Equal(ComparisonStatus.Failed, ScanComparison.Compare(Result("mft", pretender), Result("generic")).Status);
    }

    [Fact]
    public void HardlinkAllocation_IsComparedByFullIdentityWithoutRequiringSameBillingPath()
    {
        FsNode a = File("a", 100), b = File("b", 0), peerA = File("a", 0), peerB = File("b", 100);
        foreach (FsNode node in new[] { a, b, peerA, peerB })
        {
            node.FileKey = 42;
            node.FileKeyHigh = 7;
            node.VolumeKey = 123;
            node.Flags |= NodeFlags.Hardlinked;
        }
        Assert.Equal(ComparisonStatus.Passed, ScanComparison.Compare(Result("mft", a, b), Result("generic", peerA, peerB)).Status);
        peerB.FileKeyHigh = 8;
        Assert.Equal(ComparisonStatus.Failed, ScanComparison.Compare(Result("mft", Clone(a), Clone(b)), Result("generic", Clone(peerA), Clone(peerB))).Status);
    }

    [Fact]
    public void RejectsInvalidStoredRootAggregates()
    {
        ScanResult primary = Result("mft", File("data", 100));
        primary.Root.TotalAllocated = 101;
        Assert.Equal(ComparisonStatus.Failed, ScanComparison.Compare(primary, Result("generic", File("data", 100))).Status);
    }

    [Fact]
    public void SelfComparisonMissingReferenceAndPartialScans_AreInconclusive()
    {
        Assert.Equal(ComparisonStatus.Inconclusive, ScanComparison.Compare(Result("generic"), Result("generic")).Status);
        Assert.Equal(ComparisonStatus.Inconclusive, ScanComparison.Compare(Result("mft"), null).Status);
        var partial = new ScanResult
        {
            Root = Result("generic").Root, Volume = Volume, Stats = new ScanStats { ErrorCount = 1 },
            EngineName = "generic", Completeness = ScanCompleteness.Partial,
        };
        Assert.Equal(ComparisonStatus.Inconclusive, ScanComparison.Compare(Result("mft"), partial).Status);
    }

    private static VolumeInfo Volume => new() { RootPath = "C:\\", DisplayName = "Test", VolumeSerialNumber = 123 };
    private static FsNode File(string name, ulong size) => new() { Name = name, AllocatedSize = size, LogicalSize = size };
    private static FsNode Clone(FsNode node) => new()
    {
        Name = node.Name, AllocatedSize = node.AllocatedSize, LogicalSize = node.LogicalSize,
        FileKey = node.FileKey, FileKeyHigh = node.FileKeyHigh, VolumeKey = node.VolumeKey, Flags = node.Flags,
    };

    private static ScanResult Result(string engine, params FsNode[] children)
    {
        var root = new FsNode { Name = "C:\\", Flags = NodeFlags.Directory };
        foreach (FsNode child in children) root.AddChild(child);
        TreeOps.Finalize(root);
        return new ScanResult
        {
            Root = root, Volume = Volume, Stats = new ScanStats(), EngineName = engine,
            AccountingFeatures = AccountingFeatures.UnnamedData | AccountingFeatures.FileIdentity |
                (engine == "mft" ? AccountingFeatures.NamedStreams | AccountingFeatures.DirectoryMetadata : 0),
        };
    }
}
