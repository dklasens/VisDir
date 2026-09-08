namespace VisDir.Core;

/// <summary>
/// A node in the file-system tree. Plain fields: this type is allocated millions of times.
/// Sizes are "self" values; Total* aggregates are computed by <see cref="TreeOps.Finalize"/>.
/// </summary>
public sealed class FsNode
{
    public string Name = string.Empty;
    public FsNode? Parent;
    public List<FsNode>? Children;
    public ulong LogicalSize;
    public ulong AllocatedSize;
    public ulong TotalLogical;
    public ulong TotalAllocated;
    public NodeFlags Flags;
    /// <summary>NTFS file reference number (low 64 bits) or generic FileId when available.</summary>
    public long FileKey;

    public bool IsDirectory => (Flags & NodeFlags.Directory) != 0;

    public void AddChild(FsNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (ReferenceEquals(child, this)) throw new ArgumentException("A node cannot be its own child.", nameof(child));
        if (child.Parent is not null) throw new InvalidOperationException("Child already has a parent; re-parenting is not allowed.");
        child.Parent = this;
        (Children ??= new List<FsNode>()).Add(child);
        Flags &= ~NodeFlags.ChildrenSorted;
    }

    /// <summary>Presizes the child list so a known batch appends without regrowth.</summary>
    public void EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (Children is null)
        {
            if (capacity > 0) Children = new List<FsNode>(capacity);
        }
        else Children.EnsureCapacity(capacity);
    }

    /// <summary>Appends a batch with a single presize; same per-child guards as <see cref="AddChild"/>.</summary>
    public void AddChildren(IEnumerable<FsNode> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children is ICollection<FsNode> batch)
            EnsureCapacity((Children?.Count ?? 0) + batch.Count);
        foreach (FsNode child in children)
        {
            ArgumentNullException.ThrowIfNull(child);
            if (ReferenceEquals(child, this)) throw new ArgumentException("A node cannot be its own child.", nameof(child));
            if (child.Parent is not null) throw new InvalidOperationException("Child already has a parent; re-parenting is not allowed.");
            child.Parent = this;
            (Children ??= new List<FsNode>()).Add(child);
        }
        Flags &= ~NodeFlags.ChildrenSorted;
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
            // Root always keeps its trailing backslash; deeper nodes only when directories.
            if (first || n.IsDirectory)
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '\\') sb.Append('\\');
            }
            first = false;
        }
        return sb.ToString();
    }
}
