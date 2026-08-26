namespace VisDir.Core;

public static class TreeOps
{
    /// <summary>
    /// Iterative post-order pass: computes Total* aggregates and sorts children
    /// descending by TotalAllocated (DaisyDisk-style ordering).
    /// </summary>
    public static void Finalize(FsNode root)
    {
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

            ulong tl = frame.Node.LogicalSize;
            ulong ta = frame.Node.AllocatedSize;
            if (frame.Node.Children is { Count: > 0 } children)
            {
                foreach (FsNode c in children)
                {
                    tl += c.TotalLogical;
                    ta += c.TotalAllocated;
                }
                if (children.Count > 1)
                {
                    children.Sort(static (a, b) =>
                    {
                        int cmp = b.TotalAllocated.CompareTo(a.TotalAllocated);
                        return cmp != 0 ? cmp : string.CompareOrdinal(a.Name, b.Name);
                    });
                }
            }
            frame.Node.TotalLogical = tl;
            frame.Node.TotalAllocated = ta;
        }
    }
}
