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
        child.Parent = this;
        (Children ??= new List<FsNode>()).Add(child);
    }

    public string GetPath()
    {
        if (Parent is null) return Name;
        var sb = new System.Text.StringBuilder(260);
        BuildPath(sb);
        return sb.ToString();
    }

    private void BuildPath(System.Text.StringBuilder sb)
    {
        if (Parent is null)
        {
            sb.Append(Name);
            if (sb.Length > 0 && sb[sb.Length - 1] != '\\') sb.Append('\\');
            return;
        }
        Parent.BuildPath(sb);
        sb.Append(Name);
        if (IsDirectory) sb.Append('\\');
    }
}
