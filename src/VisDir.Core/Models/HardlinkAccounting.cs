namespace VisDir.Core;

/// <summary>Both engines attribute allocation to the first full path in the requested scan scope.</summary>
public static class HardlinkAccounting
{
    public static int ComparePaths(string left, string right)
    {
        int comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
    }

    /// <param name="onlyMarkedHardlinks">For an MFT tree before its first accounting pass,
    /// where the parser has already marked every entry belonging to a multi-name record.</param>
    public static ulong Apply(FsNode root, CancellationToken cancellationToken = default, bool onlyMarkedHardlinks = false)
    {
        var owners = new Dictionary<FileIdentity, FsNode>();
        var pending = new Stack<FsNode>();
        pending.Push(root);
        ulong removed = 0;
        while (pending.TryPop(out FsNode? node))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Children is { } children)
                foreach (FsNode child in children) pending.Push(child);
            if (node.IsDirectory || !node.Identity.IsKnown) continue;
            if (onlyMarkedHardlinks && (node.Flags & NodeFlags.Hardlinked) == 0) continue;
            if (!owners.TryGetValue(node.Identity, out FsNode? previous))
            {
                owners.Add(node.Identity, node);
                continue;
            }
            FsNode owner = previous, alias = node;
            if (ComparePaths(node.GetPath(), previous.GetPath()) < 0)
            {
                owner = node;
                alias = previous;
                owners[node.Identity] = node;
            }
            // Retain the largest observation when a live file changes during enumeration.
            ulong retained = Math.Max(owner.AllocatedSize, alias.AllocatedSize);
            removed = TreeOps.SaturatingAdd(removed, Math.Min(owner.AllocatedSize, alias.AllocatedSize));
            if (alias.AllocatedSize > owner.AllocatedSize)
            {
                owner.StreamAllocatedSize = alias.StreamAllocatedSize;
                owner.MetadataAllocatedSize = alias.MetadataAllocatedSize;
            }
            owner.AllocatedSize = retained;
            owner.Flags &= ~NodeFlags.Hardlinked;
            alias.AllocatedSize = alias.StreamAllocatedSize = alias.MetadataAllocatedSize = 0;
            alias.Flags |= NodeFlags.Hardlinked;
        }
        return removed;
    }
}
