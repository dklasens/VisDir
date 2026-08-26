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

    public double MidAngle => (Angle0 + Angle1) / 2;
    public double Sweep => Angle1 - Angle0;

    /// <summary>Top-level ancestor rank (size order) — drives hue assignment. -1 for root/aggregated wedges.</summary>
    public int BranchIndex;

    /// <summary>Number of visible top-level branches — spaces sibling hues evenly.</summary>
    public int BranchCount;

    public string DisplayName => Source.Name;
}

public static class SunburstLayout
{
    public const double FullCircle = 2 * Math.PI;

    /// <summary>
    /// Builds angular layout for <paramref name="viewRoot"/>, starting at angle 0.
    /// Children are already sorted descending by TotalAllocated (TreeOps.Finalize).
    /// Sub-threshold children collapse into one aggregated wedge.
    /// </summary>
    public static SunburstNode Build(FsNode viewRoot, double minSweepRadians = 0.006)
    {
        var root = new SunburstNode
        {
            Source = viewRoot,
            Angle0 = 0,
            Angle1 = FullCircle,
            Depth = 0,
        };
        LayoutChildren(root, minSweepRadians);
        AssignBranches(root);
        root.BranchIndex = -1;
        root.BranchCount = root.Children?.Count ?? 0;
        return root;
    }

    private static void LayoutChildren(SunburstNode parent, double minSweep)
    {
        FsNode src = parent.Source;
        if (src.Children is not { Count: > 0 } kids) return;

        ulong total = src.TotalAllocated;
        if (total == 0)
        {
            // Zero-size dirs (placeholders): give children equal tiny slices so they remain visible.
            double slice = Math.Max(parent.Sweep, 0.02) / kids.Count;
            double a = parent.Angle0;
            var equal = new List<SunburstNode>(kids.Count);
            foreach (FsNode k in kids.TakeWhile(_ => true))
            {
                if (a >= parent.Angle1) break;
                var n = new SunburstNode
                {
                    Source = k,
                    Angle0 = a,
                    Angle1 = Math.Min(a + slice, parent.Angle1),
                    Depth = parent.Depth + 1,
                };
                equal.Add(n);
                a += slice;
            }
            parent.Children = equal;
            return;
        }

        double span = parent.Sweep;
        var built = new List<SunburstNode>(kids.Count);
        var small = new List<FsNode>();
        ulong smallBytes = 0;
        double cursor = parent.Angle0;

        foreach (FsNode k in kids)
        {
            double sweep = span * ((double)k.TotalAllocated / total);
            if (sweep < minSweep && k != kids[0])
            {
                small.Add(k);
                smallBytes += k.TotalAllocated;
                continue;
            }

            built.Add(new SunburstNode
            {
                Source = k,
                Angle0 = cursor,
                Angle1 = cursor + sweep,
                Depth = parent.Depth + 1,
            });
            cursor += sweep;
        }

        if (small.Count > 0 && cursor < parent.Angle1 - minSweep / 2)
        {
            parent.AggregatedWedge = new SunburstNode
            {
                Source = new FsNode
                {
                    Name = "smaller objects...",
                    TotalAllocated = smallBytes,
                    Flags = NodeFlags.Directory,
                },
                IsAggregatedWedge = true,
                Angle0 = cursor,
                Angle1 = parent.Angle1,
                Depth = parent.Depth + 1,
            };
        }
        else if (built.Count > 0)
        {
            // Stretch the last node to close any rounding gap.
            built[^1].Angle1 = parent.Angle1;
        }

        parent.Children = built;
        foreach (SunburstNode child in built)
            LayoutChildren(child, minSweep);
    }

    private static void AssignBranches(SunburstNode root)
    {
        int count = root.Children?.Count ?? 0;
        if (root.Children is { } kids)
            for (int i = 0; i < kids.Count; i++)
                AssignBranch(kids[i], i, count);
        if (root.AggregatedWedge is { } wedge)
        {
            wedge.BranchIndex = -1;
            wedge.BranchCount = count;
        }
    }

    private static void AssignBranch(SunburstNode node, int index, int count)
    {
        node.BranchIndex = index;
        node.BranchCount = count;
        if (node.AggregatedWedge is { } wedge)
        {
            wedge.BranchIndex = -1;
            wedge.BranchCount = count;
        }
        if (node.Children is { } kids)
            foreach (SunburstNode k in kids)
                AssignBranch(k, index, count);
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
