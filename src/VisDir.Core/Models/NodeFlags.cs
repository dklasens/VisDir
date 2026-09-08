namespace VisDir.Core;

[Flags]
public enum NodeFlags : ushort
{
    None = 0,
    Directory = 1 << 0,
    ReparsePoint = 1 << 1,
    Hidden = 1 << 2,
    System = 1 << 3,
    Compressed = 1 << 4,
    SparseFile = 1 << 5,
    CloudPlaceholder = 1 << 6,
    AccessDenied = 1 << 7,
    OrphanedRoot = 1 << 8,
    ErrorNode = 1 << 9,
    NamedStreamExtra = 1 << 10,
    Hardlinked = 1 << 11,
    /// <summary>Transient in-memory hint: children are in <see cref="TreeOps.Finalize"/> order. Never persisted; cleared on mutation.</summary>
    ChildrenSorted = 1 << 12,
}
