using System.Collections.ObjectModel;

namespace VisDir.Core;

/// <summary>A directory entry. Builders may mutate it until the completed snapshot is frozen.</summary>
public sealed class FsNode
{
    private List<FsNode>? _children;
    private ReadOnlyCollection<FsNode>? _childrenView;
    // Most entries have no separate streams or metadata. Keep the breakdown sparse
    // instead of adding four counters to every one of millions of file nodes.
    private AllocationBreakdown? _allocation;
    private sealed class AllocationBreakdown
    {
        public ulong Streams, Metadata, TotalStreams, TotalMetadata;
    }
    public bool IsFrozen { get; private set; }
    public string Name { get; set { EnsureMutable(); field = value; InvalidateAncestors(); } } = string.Empty;
    public FsNode? Parent { get; internal set { EnsureMutable(); field = value; } }
    public IReadOnlyList<FsNode>? Children => _children is null ? null : _childrenView ??= _children.AsReadOnly();
    public ulong LogicalSize { get; set { EnsureMutable(); field = value; InvalidateAncestors(); } }
    /// <summary>Physical allocation attributed to this entry, including measured streams and metadata.</summary>
    public ulong AllocatedSize { get; set { EnsureMutable(); field = value; InvalidateAncestors(); } }
    public ulong StreamAllocatedSize
    {
        get => _allocation?.Streams ?? 0;
        set
        {
            EnsureMutable();
            if (value != 0 || _allocation is not null) (_allocation ??= new AllocationBreakdown()).Streams = value;
        }
    }
    public ulong MetadataAllocatedSize
    {
        get => _allocation?.Metadata ?? 0;
        set
        {
            EnsureMutable();
            if (value != 0 || _allocation is not null) (_allocation ??= new AllocationBreakdown()).Metadata = value;
        }
    }
    public ulong TotalLogical { get; set { EnsureMutable(); field = value; } }
    public ulong TotalAllocated { get; set { EnsureMutable(); field = value; } }
    public ulong TotalStreamAllocated
    {
        get => _allocation?.TotalStreams ?? 0;
        set
        {
            EnsureMutable();
            if (value != 0 || _allocation is not null) (_allocation ??= new AllocationBreakdown()).TotalStreams = value;
        }
    }
    public ulong TotalMetadataAllocated
    {
        get => _allocation?.TotalMetadata ?? 0;
        set
        {
            EnsureMutable();
            if (value != 0 || _allocation is not null) (_allocation ??= new AllocationBreakdown()).TotalMetadata = value;
        }
    }
    public NodeFlags Flags { get; set { EnsureMutable(); field = value; } }
    public long FileKey { get; set { EnsureMutable(); field = value; } }
    public ulong FileKeyHigh { get; set { EnsureMutable(); field = value; } }
    public ulong VolumeKey { get; set { EnsureMutable(); field = value; } }
    public FileIdentity Identity => new(VolumeKey, FileKey, FileKeyHigh);
    public bool IsDirectory => (Flags & NodeFlags.Directory) != 0;

    public void AddChild(FsNode child)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(child);
        child.EnsureMutable();
        if (ReferenceEquals(child, this)) throw new ArgumentException("A node cannot be its own child.", nameof(child));
        if (child.Parent is not null) throw new InvalidOperationException("Child already has a parent.");
        if (child._children is { Count: > 0 })
            for (FsNode? ancestor = this; ancestor is not null; ancestor = ancestor.Parent)
                if (ReferenceEquals(ancestor, child)) throw new InvalidOperationException("A tree cannot contain a cycle.");
        child.Parent = this;
        (_children ??= new List<FsNode>()).Add(child);
        InvalidateOrder();
    }

    public void EnsureCapacity(int capacity)
    {
        EnsureMutable();
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (_children is null)
        {
            if (capacity > 0) _children = new List<FsNode>(capacity);
        }
        else _children.EnsureCapacity(capacity);
    }

    public void AddChildren(IEnumerable<FsNode> children)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(children);
        if (children.TryGetNonEnumeratedCount(out int count)) EnsureCapacity(checked((_children?.Count ?? 0) + count));
        foreach (FsNode child in children) AddChild(child);
    }

    internal void SortChildren(Comparison<FsNode> comparison)
    {
        EnsureMutable();
        _children?.Sort(comparison);
    }

    internal void Freeze() => IsFrozen = true;

    private void EnsureMutable()
    {
        if (IsFrozen) throw new InvalidOperationException("A published scan snapshot is immutable.");
    }

    private void InvalidateAncestors() => Parent?.InvalidateOrder();
    private void InvalidateOrder()
    {
        for (FsNode? node = this; node is not null; node = node.Parent)
        {
            if ((node.Flags & NodeFlags.ChildrenSorted) == 0) break;
            node.Flags &= ~NodeFlags.ChildrenSorted;
        }
    }

    public string GetPath()
    {
        if (Parent is null) return Name;
        var chain = new Stack<FsNode>();
        for (FsNode? n = this; n is not null; n = n.Parent) chain.Push(n);
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (FsNode n in chain)
        {
            sb.Append(n.Name);
            if ((first || n.IsDirectory) && sb.Length > 0 && sb[^1] != '\\') sb.Append('\\');
            first = false;
        }
        return sb.ToString();
    }
}

/// <summary>File identity is volume-scoped and retains all 128 bits supplied by Windows.</summary>
public readonly record struct FileIdentity(ulong Volume, long Low, ulong High)
{
    public bool IsKnown => Low != 0 || High != 0;
}
