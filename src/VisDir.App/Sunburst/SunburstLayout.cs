using VisDir.Core;

namespace VisDir.App.Sunburst;

/// <summary>View-model node for the sunburst: precomputed angular geometry per depth ring.</summary>
public sealed class SunburstNode
{
    public required FsNode Source { get; init; }
    public double Angle0;          // radians, within [0, 2π)
    public double Angle1;
    public int Depth;

    public List<SunburstNode>? Children;
    public SunburstNode? AggregatedWedge; // "smaller items" bucket
    public bool IsAggregatedWedge;
    public FsNode? AggregateSource;

    public double MidAngle => (Angle0 + Angle1) / 2;
    public double Sweep => Angle1 - Angle0;

    /// <summary>Top-level ancestor rank (size order) — drives hue assignment. -1 for root/aggregated wedges.</summary>
    public int BranchIndex;

    /// <summary>Number of visible top-level branches — spaces sibling hues evenly.</summary>
    public int BranchCount;

    /// <summary>Starting angle (radians) of the top-level branch sector containing this node.</summary>
    public double BranchStartAngle;

    /// <summary>Ending angle (radians) of the top-level branch sector containing this node.</summary>
    public double BranchEndAngle;

    /// <summary>Zero-based index among siblings within the parent node.</summary>
    public int SiblingIndex;

    /// <summary>Total visible sibling count in the parent node.</summary>
    public int SiblingCount;

    public string DisplayName => Source.Name;
}

public static class SunburstLayout
{
    public const double FullCircle = 2 * Math.PI;
    public const double MinimumSweep = 0.006;
    public const int MaxVisibleChildren = 512;
    public const int MaxLayoutNodes = 4096;

    public static bool IsVisibleChild(FsNode parent, FsNode child, int index, int visibleCount,
        double minimumSweep = MinimumSweep) =>
        parent.TotalAllocated > 0 && child.TotalAllocated > 0 && visibleCount < MaxVisibleChildren &&
        (index == 0 || FullCircle * ((double)child.TotalAllocated / parent.TotalAllocated) >= minimumSweep);

    /// <param name="maxDepth">Deepest ring to build. Must match SunburstControl.MaxVisibleDepth;
    /// deeper subtrees rebuild lazily when the user navigates into them.</param>
    public static SunburstNode Build(FsNode viewRoot, double minSweepRadians = MinimumSweep, int maxDepth = 6, CancellationToken cancellationToken = default)
    {
        var root = new SunburstNode
        {
            Source = viewRoot,
            Angle0 = 0,
            Angle1 = FullCircle,
            Depth = 0,
            BranchStartAngle = 0,
            BranchEndAngle = FullCircle,
            SiblingIndex = 0,
            SiblingCount = 1,
        };
        int remainingNodes = MaxLayoutNodes - 1;
        LayoutChildren(root, minSweepRadians, maxDepth, ref remainingNodes, cancellationToken);
        AssignBranches(root);
        root.BranchIndex = -1;
        root.BranchCount = root.Children?.Count ?? 0;
        root.BranchStartAngle = 0;
        root.BranchEndAngle = FullCircle;
        root.SiblingIndex = 0;
        root.SiblingCount = 1;
        return root;
    }

    private static void LayoutChildren(SunburstNode parent, double minSweep, int maxDepth,
        ref int remainingNodes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parent.Depth + 1 > maxDepth || remainingNodes < 2) return;
        FsNode src = parent.Source;
        if (src.Children is not { Count: > 0 } kids || src.TotalAllocated == 0) return;

        // Zero-byte entries remain browsable in the list, but have no area to plot.
        // Reserve one node for a smaller-items group; allocations never scale with child count.
        int childBudget = Math.Min(MaxVisibleChildren, remainingNodes - 1);
        var built = new List<SunburstNode>(Math.Min(kids.Count, childBudget));
        ulong smallBytes = 0;
        int smallCount = 0;
        double cursor = parent.Angle0;
        for (int i = 0; i < kids.Count; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            FsNode child = kids[i];
            double sweep = parent.Sweep * ((double)child.TotalAllocated / src.TotalAllocated);
            if (child.TotalAllocated == 0 || built.Count >= childBudget || (sweep < minSweep && i != 0))
            {
                smallCount++;
                smallBytes = ulong.MaxValue - smallBytes < child.TotalAllocated
                    ? ulong.MaxValue : smallBytes + child.TotalAllocated;
                continue;
            }
            built.Add(new SunburstNode
            {
                Source = child, Angle0 = cursor, Angle1 = cursor + sweep, Depth = parent.Depth + 1,
            });
            cursor += sweep;
            remainingNodes--;
        }

        double smallerSweep = parent.Sweep * ((double)smallBytes / src.TotalAllocated);
        if (smallCount > 0 && smallerSweep > 0)
        {
            parent.AggregatedWedge = new SunburstNode
            {
                Source = new FsNode { Name = "smaller objects...", TotalAllocated = smallBytes, Flags = NodeFlags.Directory },
                AggregateSource = src,
                IsAggregatedWedge = true,
                Angle0 = cursor,
                Angle1 = Math.Min(parent.Angle1, cursor + smallerSweep),
                Depth = parent.Depth + 1,
            };
            remainingNodes--;
        }
        // Directory index allocation belongs to the parent, never its last child or aggregate.
        parent.Children = built;
        foreach (SunburstNode child in built)
            LayoutChildren(child, minSweep, maxDepth, ref remainingNodes, cancellationToken);
    }

    private static void AssignBranches(SunburstNode root)
    {
        int count = root.Children?.Count ?? 0;
        if (root.Children is { } kids)
        {
            for (int i = 0; i < kids.Count; i++)
            {
                kids[i].SiblingIndex = i;
                kids[i].SiblingCount = kids.Count;
                AssignBranch(kids[i], i, count, kids[i].Angle0, kids[i].Angle1);
            }
        }
        if (root.AggregatedWedge is { } wedge)
        {
            wedge.BranchIndex = -1;
            wedge.BranchCount = count;
            wedge.BranchStartAngle = wedge.Angle0;
            wedge.BranchEndAngle = wedge.Angle1;
            wedge.SiblingIndex = count;
            wedge.SiblingCount = count + 1;
        }
    }

    private static void AssignBranch(SunburstNode node, int index, int count, double bStart, double bEnd)
    {
        node.BranchIndex = index;
        node.BranchCount = count;
        node.BranchStartAngle = bStart;
        node.BranchEndAngle = bEnd;
        if (node.AggregatedWedge is { } wedge)
        {
            wedge.BranchIndex = index;
            wedge.BranchCount = count;
            wedge.BranchStartAngle = bStart;
            wedge.BranchEndAngle = bEnd;
            wedge.SiblingIndex = node.Children?.Count ?? 0;
            wedge.SiblingCount = (node.Children?.Count ?? 0) + 1;
        }
        if (node.Children is { } kids)
        {
            for (int i = 0; i < kids.Count; i++)
            {
                kids[i].SiblingIndex = i;
                kids[i].SiblingCount = kids.Count;
                AssignBranch(kids[i], index, count, bStart, bEnd);
            }
        }
    }

    /// <summary>Hit-test: returns the deepest visible node containing (radiusFraction, angle), or null.</summary>
    public static SunburstNode? HitTest(
        SunburstNode root, double radiusFraction, double angle,
        double innerRadiusFraction, double ringWidthFraction, double gapFraction, int maxDepth)
    {
        if (radiusFraction < innerRadiusFraction) return null;
        double t = radiusFraction - innerRadiusFraction;
        double band = ringWidthFraction + gapFraction;
        int depth = (int)(t / band);
        if (depth > maxDepth || t % band > ringWidthFraction) return null;
        if (depth == 0) return root;

        double a = Normalize(angle);
        SunburstNode? current = root;
        for (int d = 1; d <= depth; d++)
        {
            SunburstNode? next = FindChild(current, a);
            if (next is null) return null;
            current = next;
        }
        return current;
    }

    private static SunburstNode? FindChild(SunburstNode parent, double angle)
    {
        if (parent.AggregatedWedge is { } w &&
            angle >= w.Angle0 && angle < w.Angle1) return w;

        if (parent.Children is not { } kids) return null;
        int lo = 0, hi = kids.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            SunburstNode k = kids[mid];
            if (angle < k.Angle0) hi = mid - 1;
            else if (angle >= k.Angle1) lo = mid + 1;
            else return k;
        }
        return null;
    }

    private static double Normalize(double a)
    {
        a %= FullCircle;
        return a < 0 ? a + FullCircle : a;
    }
}
