using VisDir.Core;

namespace VisDir.Core.Tests;

public sealed class SnapshotReadProgressTests
{
    [Fact]
    public void LoadingReportsKnownCountsAndCompletesAfterValidation()
    {
        using var snapshot = Snapshot();
        var updates = new List<ScanProgress>();
        ScanResult loaded = TreeSerializer.Read(snapshot, false, default, new CallbackProgress(updates.Add));
        Assert.True(loaded.Root.IsFrozen);
        Assert.True(snapshot.CanRead);
        Assert.True(updates.Count >= 2);
        Assert.Equal(0, updates[0].Fraction);
        Assert.Equal(1, updates[^1].Fraction);
        Assert.All(updates, update =>
        {
            Assert.Equal("Loading results", update.Phase);
            Assert.Equal(10, update.FilesSeen);
            Assert.Equal(1, update.DirsSeen);
            Assert.Equal(40_960UL, update.BytesSeen);
        });
        Assert.Equal(updates.Select(p => p.Fraction).OrderBy(f => f), updates.Select(p => p.Fraction));
    }

    [Fact]
    public void CancellingAtLoadingStartStopsDeserialization()
    {
        using var snapshot = Snapshot();
        using var cancellation = new CancellationTokenSource();
        var updates = new List<ScanProgress>();
        Assert.Throws<OperationCanceledException>(() => TreeSerializer.Read(snapshot, false, cancellation.Token,
            new CallbackProgress(p => { updates.Add(p); cancellation.Cancel(); })));
        Assert.Single(updates);
        Assert.Equal(0, updates[0].Fraction);
        Assert.True(snapshot.Position < snapshot.Length);
    }

    [Fact]
    public void TruncatedResultNeverReportsSuccessfulCompletion()
    {
        using var full = Snapshot();
        using var truncated = new MemoryStream(full.ToArray()[..^10]);
        var updates = new List<ScanProgress>();
        Assert.Throws<InvalidDataException>(() => TreeSerializer.Read(truncated, false, default, new CallbackProgress(updates.Add)));
        Assert.NotEmpty(updates);
        Assert.DoesNotContain(updates, p => p.Fraction == 1);
    }

    private static MemoryStream Snapshot()
    {
        var root = new FsNode { Name = @"C:\", Flags = NodeFlags.Directory };
        for (int i = 0; i < 10; i++) root.AddChild(new FsNode { Name = $"file-{i}", AllocatedSize = 4096 });
        TreeOps.Finalize(root);
        var result = new ScanResult
        {
            Root = root, Volume = new VolumeInfo { RootPath = root.Name, DisplayName = "Test" },
            Stats = new ScanStats { FileCount = 10, DirectoryCount = 1, BytesSeen = root.TotalAllocated },
        };
        var stream = new MemoryStream();
        TreeSerializer.Write(stream, result);
        stream.Position = 0;
        return stream;
    }

    private sealed class CallbackProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
