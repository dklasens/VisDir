namespace VisDir.Core;

public static class TreeOps
{
    /// <summary>
    /// Iterative post-order pass: computes Total* aggregates and sorts children
    /// descending by TotalAllocated (DaisyDisk-style ordering).
    /// Subtrees already marked <see cref="NodeFlags.ChildrenSorted"/> skip re-sorting;
    /// any <see cref="FsNode.AddChild"/> clears the mark.
    /// </summary>
    public static void Finalize(FsNode root) => Finalize(root, sortChildren: true, recomputeTotals: true);

    /// <summary>As <see cref="Finalize(FsNode)"/>, but <c>sortChildren: false</c> preserves current child order.</summary>
    public static void Finalize(FsNode root, bool sortChildren) => Finalize(root, sortChildren, recomputeTotals: true);

    /// <summary>
    /// Full post-order pass. <c>recomputeTotals: false</c> keeps stored Total* values
    /// (still sorts when asked); <c>sortChildren: false</c> preserves child order.
    /// </summary>
    public static void Finalize(FsNode root, bool sortChildren, bool recomputeTotals)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!sortChildren && !recomputeTotals) return;

        var stack = new Stack<(FsNode Node, int Index)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var frame = stack.Pop();
            if (frame.Node.Children is { } kids && frame.Index < kids.Count)
            {
                stack.Push((frame.Node, frame.Index + 1));
                stack.Push((kids[frame.Index], 0));
                continue;
            }

            FsNode node = frame.Node;
            if (recomputeTotals)
            {
                ulong tl = node.LogicalSize;
                ulong ta = node.AllocatedSize;
                if (node.Children is { Count: > 0 } totals)
                {
                    foreach (FsNode c in totals)
                    {
                        // Saturating add: corrupt/huge subtrees pin at ulong.MaxValue instead of wrapping.
                        tl = tl > ulong.MaxValue - c.TotalLogical ? ulong.MaxValue : tl + c.TotalLogical;
                        ta = ta > ulong.MaxValue - c.TotalAllocated ? ulong.MaxValue : ta + c.TotalAllocated;
                    }
                }
                node.TotalLogical = tl;
                node.TotalAllocated = ta;
            }

            if (sortChildren && node.Children is { Count: > 0 } children)
            {
                if (children.Count > 1 && (node.Flags & NodeFlags.ChildrenSorted) == 0)
                {
                    children.Sort(static (a, b) =>
                    {
                        int cmp = b.TotalAllocated.CompareTo(a.TotalAllocated);
                        return cmp != 0 ? cmp : string.CompareOrdinal(a.Name, b.Name);
                    });
                }
                node.Flags |= NodeFlags.ChildrenSorted;
            }
        }
    }
}
