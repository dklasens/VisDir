using System.Collections.ObjectModel;
using VisDir.Core;

namespace VisDir.Core.Tests;

public class CoreArchitectureTests
{
    [Fact]
    public void FrozenSnapshotRejectsMutationAcrossEntireTree()
    {
        FsNode root = Directory("C:\\");
        var child = File("file", 4096, 1);
        root.AddChild(child);
        TreeOps.Finalize(root);
        TreeOps.Freeze(root);
        Assert.True(root.IsFrozen);
        Assert.True(child.IsFrozen);
        Assert.Throws<InvalidOperationException>(() => child.Name = "changed");
        Assert.Throws<InvalidOperationException>(() => child.AllocatedSize = 0);
        Assert.Throws<InvalidOperationException>(() => child.StreamAllocatedSize = 0);
        Assert.Throws<InvalidOperationException>(() => child.Parent = null);
        Assert.Throws<InvalidOperationException>(() => root.AddChild(File("new", 1, 2)));
        Assert.Throws<NotSupportedException>(() => ((IList<FsNode>)root.Children!).Clear());
        Assert.Equal(4096UL, root.TotalAllocated);
    }

    [Fact]
    public void EmptyDirectoryGrowthInvalidatesAncestorOrder()
    {
        FsNode root = Directory("C:\\"), growing = Directory("growing");
        root.AddChild(growing);
        root.AddChild(File("small", 100, 1));
        TreeOps.Finalize(root);
        Assert.Equal("small", root.Children![0].Name);
        growing.AddChild(File("large", 8192, 2));
        TreeOps.Finalize(root);
        Assert.Same(growing, root.Children![0]);
    }

    [Fact]
    public void DescendantAllocationChangesResortAllAffectedAncestors()
    {
        FsNode root = Directory("C:\\"), a = Directory("A"), nested = Directory("nested"), b = Directory("B");
        var leaf = File("growing", 1, 1);
        root.AddChildren(new[] { a, b });
        a.AddChild(nested); nested.AddChild(leaf); b.AddChild(File("steady", 100, 2));
        TreeOps.Finalize(root);
        Assert.Same(b, root.Children![0]);
        leaf.AllocatedSize = 8192;
        TreeOps.Finalize(root);
        Assert.Same(a, root.Children![0]);
        Assert.Equal(8292UL, root.TotalAllocated);
    }

    [Fact]
    public void BuilderCannotCreateAncestorCycle()
    {
        FsNode root = Directory("root"), child = Directory("child");
        root.AddChild(child);
        Assert.Throws<InvalidOperationException>(() => child.AddChild(root));
        Assert.Null(root.Parent);
    }

    [Fact]
    public void IdentitiesRetainHighBitsAndVolumeScope()
    {
        FsNode root = Directory("C:\\");
        var first = File("first", 4096, 42);
        var otherHigh = File("high", 4096, 42); otherHigh.FileKeyHigh = 1;
        var otherVolume = File("volume", 4096, 42); otherVolume.VolumeKey = 2;
        root.AddChildren(new[] { first, otherHigh, otherVolume });
        Assert.Equal(0UL, HardlinkAccounting.Apply(root));
        TreeOps.Finalize(root);
        Assert.Equal(12_288UL, root.TotalAllocated);
    }

    [Fact]
    public void HardlinksUseDeterministicCaseTieAndTransferAllocationComponents()
    {
        FsNode root = Directory("C:\\");
        var canonical = File("A", 4096, 42);
        var alias = File("a", 8192, 42);
        alias.StreamAllocatedSize = 4096;
        alias.MetadataAllocatedSize = 1024;
        root.AddChildren(new[] { canonical, alias });
        Assert.Equal(4096UL, HardlinkAccounting.Apply(root));
        TreeOps.Finalize(root);
        Assert.Equal(8192UL, canonical.AllocatedSize);
        Assert.Equal(4096UL, canonical.StreamAllocatedSize);
        Assert.Equal(1024UL, canonical.MetadataAllocatedSize);
        Assert.Equal(0UL, alias.AllocatedSize);
        Assert.Equal(0UL, alias.StreamAllocatedSize);
        Assert.Equal(8192UL, root.TotalAllocated);
        Assert.Equal(0UL, HardlinkAccounting.Apply(root));
    }

    [Fact]
    public void CoordinatorFallsBackAndPublishesImmutableDiagnostics()
    {
        var failed = new StubScanner((_, _) => throw new IOException("Incomplete MFT coverage"));
        var compatible = new StubScanner((_, _) => Result(errorCount: 1));
        var choices = new List<string>();
        var coordinator = new ScanCoordinator(_ => Volume, () => true, engine => engine == "mft" ? failed : compatible);
        ScanResult result = coordinator.Run(new ScanOptions { Path = "C:\\" }, "auto", CancellationToken.None, null,
            selection => choices.Add(selection.Name));
        Assert.Equal(new[] { "mft", "generic" }, choices);
        Assert.Equal("Incomplete MFT coverage", result.FallbackReason);
        Assert.Equal(ScanCompleteness.Partial, result.Completeness);
        Assert.True(result.Root.IsFrozen);
        Assert.True(failed.Disposed);
        Assert.True(compatible.Disposed);
        Assert.IsType<ReadOnlyDictionary<string, double>>(result.Stats.PhaseMs);
        Assert.Throws<NotSupportedException>(() => ((IList<ScanIssue>)result.Issues).Clear());
    }

    [Fact]
    public void CoordinatorDoesNotTurnCancellationIntoASecondScan()
    {
        int compatibleCalls = 0;
        var scanner = new StubScanner((_, _) => throw new OperationCanceledException());
        var coordinator = new ScanCoordinator(_ => Volume, () => true, engine =>
        {
            if (engine == "generic") compatibleCalls++;
            return scanner;
        });
        Assert.Throws<OperationCanceledException>(() => coordinator.Run(
            new ScanOptions { Path = "C:\\" }, "auto", CancellationToken.None, null));
        Assert.Equal(0, compatibleCalls);
        Assert.True(scanner.Disposed);
    }

    [Fact]
    public void CoordinatorDoesNotPublishResultCancelledAtCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        ScanResult built = Result();
        var scanner = new StubScanner((_, _) => { cancellation.Cancel(); return built; });
        var coordinator = new ScanCoordinator(_ => Volume, () => false, _ => scanner);
        Assert.Throws<OperationCanceledException>(() => coordinator.Run(
            new ScanOptions { Path = "C:\\" }, "generic", cancellation.Token, null));
        Assert.False(built.Root.IsFrozen);
        Assert.True(scanner.Disposed);
    }

    [Fact]
    public void V3SnapshotRoundTripsIdentityAccountingDiagnosticsAndFreeze()
    {
        ScanResult original = Result(errorCount: 1);
        FsNode child = original.Root.Children![0];
        child.FileKeyHigh = 0xfedcba9876543210;
        child.VolumeKey = 0x123456789abcdef0;
        child.StreamAllocatedSize = 1024;
        child.MetadataAllocatedSize = 512;
        TreeOps.Finalize(original.Root);
        using var stream = new MemoryStream();
        TreeSerializer.Write(stream, original);
        stream.Position = 0;
        ScanResult loaded = TreeSerializer.Read(stream);
        FsNode restored = Assert.Single(loaded.Root.Children!);
        Assert.Equal(child.Identity, restored.Identity);
        Assert.Equal(1024UL, loaded.Root.TotalStreamAllocated);
        Assert.Equal(512UL, loaded.Root.TotalMetadataAllocated);
        Assert.Equal(original.Issues[0], Assert.Single(loaded.Issues));
        Assert.Equal(1.25, loaded.Stats.PhaseMs["enumerate"]);
        Assert.Equal(ScanCompleteness.Partial, loaded.Completeness);
        Assert.Equal(original.AccountingFeatures, loaded.AccountingFeatures);
        Assert.True(restored.IsFrozen);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void V2SnapshotStillLoadsAndFreezes()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(TreeSerializer.Magic); writer.Write(2u);
            Text(writer, "C:\\"); Text(writer, "Legacy"); Text(writer, "NTFS");
            writer.Write(123UL); writer.Write(4096u); writer.Write(8192UL); writer.Write(4096UL);
            Text(writer, "generic"); writer.Write(1L); writer.Write(1L); writer.Write(0L); writer.Write(4096UL); writer.Write(5.0);
            writer.Write(2UL);
            LegacyNode(writer, NodeFlags.Directory, 0, 0, 1, "C:\\");
            LegacyNode(writer, NodeFlags.None, 42, 4096, 0, "file");
        }
        stream.Position = 0;
        ScanResult loaded = TreeSerializer.Read(stream);
        Assert.Equal(4096UL, loaded.Root.TotalAllocated);
        Assert.Equal(123UL, loaded.Root.Children![0].VolumeKey);
        Assert.True(loaded.Root.IsFrozen);
        Assert.Equal(0UL, loaded.Root.TotalStreamAllocated);
    }

    [Fact]
    public void SnapshotPreservesExplicitUnknownNodeVolumeIdentity()
    {
        ScanResult sample = Result();
        sample.Root.Children![0].VolumeKey = 0;
        var original = new ScanResult
        {
            Root = sample.Root, Volume = sample.Volume with { VolumeSerialNumber = 123 }, Stats = sample.Stats,
        };
        using var stream = new MemoryStream();
        TreeSerializer.Write(stream, original); stream.Position = 0;
        ScanResult loaded = TreeSerializer.Read(stream);
        Assert.Equal(0UL, loaded.Root.Children![0].VolumeKey);
        Assert.Equal(original.Root.Children![0].Identity, loaded.Root.Children[0].Identity);
    }

    [Fact]
    public void FinalizeAndSnapshotReadHonorCancellation()
    {
        ScanResult result = Result();
        using var stream = new MemoryStream();
        TreeSerializer.Write(stream, result); stream.Position = 0;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => TreeOps.Finalize(result.Root, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => TreeSerializer.Read(stream, true, cancellation.Token));
    }

    private static FsNode Directory(string name) => new() { Name = name, Flags = NodeFlags.Directory };
    private static FsNode File(string name, ulong allocation, long id) => new()
    { Name = name, LogicalSize = allocation, AllocatedSize = allocation, FileKey = id, VolumeKey = 1 };
    private static readonly VolumeInfo Volume = new() { RootPath = "C:\\", DisplayName = "Test", FileSystemName = "NTFS" };

    private static ScanResult Result(long errorCount = 0)
    {
        FsNode root = Directory("C:\\"); root.AddChild(File("file", 4096, 42)); TreeOps.Finalize(root);
        return new ScanResult
        {
            Root = root, Volume = Volume, EngineName = "generic",
            Stats = new ScanStats { FileCount = 1, DirectoryCount = 1, ErrorCount = errorCount, PhaseMs = new Dictionary<string, double> { ["enumerate"] = 1.25 } },
            Issues = new[] { new ScanIssue("C:\\denied", "Access denied") },
            Completeness = errorCount > 0 ? ScanCompleteness.Partial : ScanCompleteness.Complete,
            AccountingFeatures = AccountingFeatures.UnnamedData | AccountingFeatures.NamedStreams | AccountingFeatures.FileIdentity,
        };
    }

    private sealed class StubScanner(Func<ScanOptions, CancellationToken, ScanResult> run) : IDiskScanner, IDisposable
    {
        public bool Disposed { get; private set; }
        public ScanResult Scan(ScanOptions options, CancellationToken ct, IProgress<ScanProgress>? progress) => run(options, ct);
        public void Dispose() => Disposed = true;
    }

    private static void Text(BinaryWriter writer, string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes);
    }

    private static void LegacyNode(BinaryWriter writer, NodeFlags flags, long key, ulong size, uint children, string name)
    {
        writer.Write((ushort)flags); writer.Write((ushort)0); writer.Write(key);
        writer.Write(size); writer.Write(size); writer.Write(4096UL); writer.Write(4096UL); writer.Write(children); Text(writer, name);
    }
}
