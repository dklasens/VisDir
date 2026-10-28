using VisDir.Core;

namespace VisDir.Scanner;

internal enum ComparisonStatus { Passed, Failed, Inconclusive }
internal sealed record ComparisonResult(ComparisonStatus Status, IReadOnlyList<string> Details,
    long MismatchCount = 0, decimal PrimaryBytes = 0, decimal ReferenceBytes = 0, decimal ExcludedBytes = 0);

/// <summary>Compares the same allocation categories and every path, with hardlink bytes grouped by identity.</summary>
internal static class ScanComparison
{
    private const decimal Tolerance = 0.01m;
    private const ulong RecordMask = 0x0000_FFFF_FFFF_FFFF;
    private static readonly string[] MetadataNames =
        ["$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", ".", "$Bitmap", "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend"];

    internal static ComparisonResult Compare(ScanResult primary, ScanResult? reference)
    {
        if (primary.EngineName != "mft" || primary.FallbackReason is not null)
            return Inconclusive("Verification requires a completed MFT scan and an independent Compatible scan; fallback or comparing an engine with itself cannot verify accuracy.");
        if (reference is null)
            return Inconclusive("The reference scan did not complete.");
        if (reference.EngineName != "generic") return Inconclusive("The reference must use the Compatible engine.");
        if (primary.Completeness != ScanCompleteness.Complete || reference.Completeness != ScanCompleteness.Complete ||
            primary.Stats.ErrorCount != 0 || reference.Stats.ErrorCount != 0)
            return Inconclusive("At least one scan is partial or contains errors; resolve these before comparing allocation.");
        if (!string.Equals(primary.Volume.RootPath.TrimEnd('\\', '/'), reference.Volume.RootPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
            (primary.Volume.VolumeSerialNumber != 0 && reference.Volume.VolumeSerialNumber != 0 && primary.Volume.VolumeSerialNumber != reference.Volume.VolumeSerialNumber))
            return Inconclusive("The scans refer to different roots or volumes.");

        var details = new List<string>();
        long mismatches = 0;
        void Mismatch(string message)
        {
            mismatches++;
            if (details.Count < 30) details.Add(message);
        }
        AccountingFeatures common = primary.AccountingFeatures & reference.AccountingFeatures;
        Dictionary<string, FsNode> left = Index(primary.Root);
        Dictionary<string, FsNode> right = Index(reference.Root);
        if (!Reconciles(primary.Root, left.Values) || !Reconciles(reference.Root, right.Values))
            return new ComparisonResult(ComparisonStatus.Failed, ["Stored root allocation does not reconcile with the sum of node allocations."], 1);

        decimal excluded = 0;
        // Only real, known NTFS metadata record numbers at the volume root qualify.
        // A dollar-prefixed filename or an arbitrary positive difference is not an allowance.
        string[] metadataPaths = left.Keys.Where(path => !right.ContainsKey(path) && IsMetadataPath(path, left, primary.Volume.RootPath)).ToArray();
        foreach (string path in metadataPaths)
        {
            excluded += left[path].AllocatedSize;
            left.Remove(path);
        }

        bool identities = (common & AccountingFeatures.FileIdentity) != 0;
        var leftGroups = new Dictionary<FileIdentity, decimal>();
        var rightGroups = new Dictionary<FileIdentity, decimal>();
        decimal primaryBytes = 0, referenceBytes = 0;
        foreach ((string path, FsNode node) in left)
        {
            if (!TryComparableBytes(node, common, out decimal bytes))
                return new ComparisonResult(ComparisonStatus.Failed, [$"Allocation subsets exceed the allocation for {path}."], 1);
            primaryBytes += bytes;
            excluded += node.AllocatedSize - bytes;
            if (identities && HasIdentity(node) && !node.IsDirectory) Add(leftGroups, node.Identity, bytes);
            if (!right.TryGetValue(path, out FsNode? peer))
            {
                Mismatch($"Missing from Compatible scan: {path}");
                continue;
            }
            if (node.IsDirectory != peer.IsDirectory)
            {
                Mismatch($"File/directory type differs: {path}");
                continue;
            }
            if (!TryComparableBytes(peer, common, out decimal peerBytes))
                return new ComparisonResult(ComparisonStatus.Failed, [$"Allocation subsets exceed the allocation for {path}."], 1);
            if (identities && HasIdentity(node) && HasIdentity(peer) && !node.IsDirectory)
            {
                if (node.Identity != peer.Identity) Mismatch($"File identity differs (the file may have changed between scans): {path}");
            }
            else if (Different(bytes, peerBytes)) Mismatch($"Allocation differs: {path}: MFT={bytes:N0}, Compatible={peerBytes:N0} bytes");
        }
        foreach ((string path, FsNode node) in right)
        {
            if (!TryComparableBytes(node, common, out decimal bytes))
                return new ComparisonResult(ComparisonStatus.Failed, [$"Allocation subsets exceed the allocation for {path}."], 1);
            referenceBytes += bytes;
            if (identities && HasIdentity(node) && !node.IsDirectory) Add(rightGroups, node.Identity, bytes);
            if (!left.ContainsKey(path)) Mismatch($"Missing from MFT scan: {path}");
        }
        if (identities)
        {
            foreach ((FileIdentity identity, decimal bytes) in leftGroups)
            {
                if (rightGroups.TryGetValue(identity, out decimal peerBytes) && Different(bytes, peerBytes))
                    Mismatch($"Allocation differs for file identity {identity}: MFT={bytes:N0}, Compatible={peerBytes:N0} bytes");
            }
        }
        if (Different(primaryBytes, referenceBytes))
            Mismatch($"Comparable root allocation differs: MFT={primaryBytes:N0}, Compatible={referenceBytes:N0} bytes");
        return new ComparisonResult(mismatches == 0 ? ComparisonStatus.Passed : ComparisonStatus.Failed,
            details, mismatches, primaryBytes, referenceBytes, excluded);
    }

    private static ComparisonResult Inconclusive(string detail) => new(ComparisonStatus.Inconclusive, [detail]);
    private static bool Different(decimal a, decimal b) => Math.Abs(a - b) > Math.Max(a, b) * Tolerance;
    private static bool HasIdentity(FsNode node) => node.FileKey != 0 || node.FileKeyHigh != 0;
    private static void Add(Dictionary<FileIdentity, decimal> groups, FileIdentity identity, decimal bytes)
        => groups[identity] = groups.GetValueOrDefault(identity) + bytes;

    private static bool TryComparableBytes(FsNode node, AccountingFeatures common, out decimal bytes)
    {
        bytes = node.AllocatedSize;
        if ((decimal)node.StreamAllocatedSize + node.MetadataAllocatedSize > bytes) return false;
        if ((common & AccountingFeatures.NamedStreams) == 0) bytes -= node.StreamAllocatedSize;
        if ((common & AccountingFeatures.DirectoryMetadata) == 0) bytes -= node.MetadataAllocatedSize;
        return true;
    }

    private static Dictionary<string, FsNode> Index(FsNode root)
    {
        var result = new Dictionary<string, FsNode>(StringComparer.Ordinal);
        var stack = new Stack<(FsNode Node, string Path)>();
        stack.Push((root, ""));
        while (stack.TryPop(out var item))
        {
            result.Add(item.Path, item.Node);
            if (item.Node.Children is not { } children) continue;
            foreach (FsNode child in children)
                stack.Push((child, item.Path.Length == 0 ? child.Name : item.Path + "\\" + child.Name));
        }
        return result;
    }

    private static bool Reconciles(FsNode root, IEnumerable<FsNode> nodes)
    {
        decimal total = 0, streams = 0, metadata = 0;
        foreach (FsNode node in nodes)
        {
            total += node.AllocatedSize;
            streams += node.StreamAllocatedSize;
            metadata += node.MetadataAllocatedSize;
        }
        return total == root.TotalAllocated && streams == root.TotalStreamAllocated && metadata == root.TotalMetadataAllocated;
    }

    private static bool IsMetadataPath(string path, IReadOnlyDictionary<string, FsNode> nodes, string root)
    {
        if (path.Length == 0 || root.Length != 3 || root[1] != ':') return false;
        string topName = path.Split('\\', 2)[0];
        if (!nodes.TryGetValue(topName, out FsNode? top)) return false;
        ulong record = (ulong)top.FileKey & RecordMask;
        return record < (ulong)MetadataNames.Length && record != 5 &&
            string.Equals(top.Name, MetadataNames[(int)record], StringComparison.Ordinal);
    }
}
