using VisDir.App.Sunburst;
using VisDir.Core;

namespace VisDir.App;

/// <summary>A bounded projection of an immutable scan snapshot. Only one page gets row models.</summary>
public sealed record DirectoryPage(
    IReadOnlyList<FsNode> Nodes, IReadOnlyDictionary<FsNode, int> BranchRanks,
    int PageIndex, int PageCount, int MatchCount, int TotalCount,
    int SmallerCount, ulong SmallerBytes, bool IncludesSmallerGroup)
{
    public const int PageSize = 200;
}

public static class DirectoryViewModel
{
    public static DirectoryPage Build(FsNode root, string query, bool showAll, int pageIndex,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FsNode> children = root.Children ?? [];
        query = query.Trim();
        pageIndex = Math.Max(0, pageIndex);
        long start = (long)pageIndex * DirectoryPage.PageSize;
        var page = new List<FsNode>(DirectoryPage.PageSize);
        var ranks = new Dictionary<FsNode, int>(ReferenceEqualityComparer.Instance);
        int matches = 0, smallerCount = 0;
        ulong smallerBytes = 0;
        bool expand = showAll || query.Length > 0 || root.TotalAllocated == 0;
        for (int i = 0; i < children.Count; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            FsNode child = children[i];
            bool visible = SunburstLayout.IsVisibleChild(root, child, i, ranks.Count);
            if (visible) ranks[child] = ranks.Count;
            else
            {
                smallerCount++;
                smallerBytes = ulong.MaxValue - smallerBytes < child.TotalAllocated
                    ? ulong.MaxValue : smallerBytes + child.TotalAllocated;
            }
            if ((!expand && !visible) || !child.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (matches >= start && page.Count < DirectoryPage.PageSize) page.Add(child);
            matches++;
        }
        bool group = !expand && smallerCount > 0;
        bool includesGroup = group && matches >= start && page.Count < DirectoryPage.PageSize;
        if (group) matches++;
        int pages = Math.Max(1, (int)(((long)matches + DirectoryPage.PageSize - 1) / DirectoryPage.PageSize));
        if (pageIndex >= pages)
            return Build(root, query, showAll, pages - 1, cancellationToken);
        return new DirectoryPage(page, ranks, pageIndex, pages, matches, children.Count,
            smallerCount, smallerBytes, includesGroup);
    }
}

/// <summary>Invalidates all work and references belonging to a previous directory or scan.</summary>
public sealed class DirectoryViewSession : IDisposable
{
    private CancellationTokenSource _cancellation = new();
    public FsNode? Root { get; private set; }
    public long Generation { get; private set; }
    public CancellationToken Token => _cancellation.Token;

    public void Reset(FsNode? root)
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        _cancellation = new CancellationTokenSource();
        Root = root;
        Generation++;
    }

    public bool IsCurrent(long generation, FsNode root) =>
        generation == Generation && ReferenceEquals(Root, root);

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        Root = null;
        Generation++;
    }
}
