using VisDir.App;
using VisDir.App.Sunburst;

namespace VisDir.Core.Tests;

public class DirectoryViewTests
{
    [Fact]
    public void ZeroAllocationDirectory_IsPagedWithoutPerFileWedgesOrBrushes()
    {
        FsNode root = MakeDirectory(100_000, 0);
        DirectoryPage page = DirectoryViewModel.Build(root, "", false, 0);
        Assert.Equal(100_000, page.MatchCount);
        Assert.Equal(DirectoryPage.PageSize, page.Nodes.Count);
        Assert.Empty(page.BranchRanks);
        Assert.False(page.IncludesSmallerGroup);
        Assert.Null(SunburstLayout.Build(root).Children);
    }

    [Fact]
    public void Filtering_PreservesEveryMatchAcrossBoundedPages()
    {
        FsNode root = MakeDirectory(1250, 4096);
        var found = new HashSet<FsNode>(ReferenceEqualityComparer.Instance);
        int pages = DirectoryViewModel.Build(root, "item", false, 0).PageCount;
        for (int i = 0; i < pages; i++)
        {
            DirectoryPage page = DirectoryViewModel.Build(root, "item", false, i);
            Assert.InRange(page.Nodes.Count, 1, DirectoryPage.PageSize);
            Assert.Equal(1250, page.MatchCount);
            foreach (FsNode node in page.Nodes) Assert.True(found.Add(node));
        }
        Assert.Equal(1250, found.Count);
    }

    [Fact]
    public void SmallerItems_CanBeExpandedWithoutChangingSnapshot()
    {
        FsNode root = MakeDirectory(2000, 4096);
        FsNode[] original = root.Children!.ToArray();
        DirectoryPage grouped = DirectoryViewModel.Build(root, "", false, 0);
        Assert.True(grouped.IncludesSmallerGroup);
        Assert.Equal(1999, grouped.SmallerCount);
        DirectoryPage expanded = DirectoryViewModel.Build(root, "", true, 0);
        Assert.Equal(2000, expanded.MatchCount);
        Assert.Equal(DirectoryPage.PageSize, expanded.Nodes.Count);
        Assert.False(expanded.IncludesSmallerGroup);
        Assert.Equal(original, root.Children);
        Assert.All(root.Children!, child => Assert.Same(root, child.Parent));
        Assert.Same(root, SunburstLayout.Build(root).AggregatedWedge!.AggregateSource);
    }

    [Fact]
    public void DirectoryAllocation_IsNotAssignedToLastChildOrSmallerGroup()
    {
        var root = new FsNode { Name = "folder", Flags = NodeFlags.Directory, AllocatedSize = 4096 };
        root.AddChild(new FsNode { Name = "child", AllocatedSize = 4096 });
        TreeOps.Finalize(root);
        SunburstNode layout = SunburstLayout.Build(root);
        Assert.Equal(Math.PI, Assert.Single(layout.Children!).Sweep, 10);

        root.AddChild(new FsNode { Name = "small", AllocatedSize = 1 });
        TreeOps.Finalize(root);
        layout = SunburstLayout.Build(root);
        Assert.NotNull(layout.AggregatedWedge);
        Assert.Equal(SunburstLayout.FullCircle / 8193, layout.AggregatedWedge.Sweep, 10);
        Assert.True(layout.AggregatedWedge.Angle1 < Math.PI + 0.01);
    }

    [Fact]
    public void LayoutBudget_AppliesEvenWhenMinimumSweepIsDisabled()
    {
        FsNode root = MakeDirectory(3000, 4096);
        foreach (FsNode child in root.Children!)
        {
            child.Flags |= NodeFlags.Directory;
            for (int i = 0; i < 10; i++) child.AddChild(new FsNode { Name = $"nested-{i}", AllocatedSize = 4096 });
        }
        TreeOps.Finalize(root);
        SunburstNode layout = SunburstLayout.Build(root, minSweepRadians: 0);
        var stack = new Stack<SunburstNode>();
        stack.Push(layout);
        int count = 0;
        while (stack.TryPop(out SunburstNode? node))
        {
            count++;
            if (node.Children is { } children)
            {
                Assert.InRange(children.Count, 0, SunburstLayout.MaxVisibleChildren);
                foreach (SunburstNode child in children) stack.Push(child);
            }
            if (node.AggregatedWedge is { } aggregate) stack.Push(aggregate);
        }
        Assert.InRange(count, 1, SunburstLayout.MaxLayoutNodes);
    }

    [Fact]
    public void LeavingView_CancelsOutstandingWorkAndReleasesItsRoot()
    {
        using var session = new DirectoryViewSession();
        FsNode root = MakeDirectory(1, 1);
        session.Reset(root);
        long generation = session.Generation;
        CancellationToken token = session.Token;
        Assert.True(session.IsCurrent(generation, root));
        session.Reset(null);
        Assert.True(token.IsCancellationRequested);
        Assert.False(session.IsCurrent(generation, root));
        Assert.Null(session.Root);
        Assert.Throws<OperationCanceledException>(() => DirectoryViewModel.Build(root, "", false, 0, token));
        Assert.Throws<OperationCanceledException>(() => SunburstLayout.Build(root, cancellationToken: token));
    }

    private static FsNode MakeDirectory(int count, ulong bytes)
    {
        var root = new FsNode { Name = "folder", Flags = NodeFlags.Directory };
        for (int i = 0; i < count; i++)
            root.AddChild(new FsNode { Name = $"item-{i:D6}", AllocatedSize = bytes });
        TreeOps.Finalize(root);
        return root;
    }
}
