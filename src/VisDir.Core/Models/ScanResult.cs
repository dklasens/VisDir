using System.Diagnostics.CodeAnalysis;

namespace VisDir.Core;

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global")]
public sealed record ScanProgress(
    long FilesSeen,
    long DirsSeen,
    ulong BytesSeen,
    double ElapsedMs,
    double Fraction = -1,
    string Phase = "Scanning");

[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public sealed record ScanStats
{
    public long FileCount { get; init; }
    public long DirectoryCount { get; init; }
    public long ErrorCount { get; init; }
    public ulong BytesSeen { get; init; }
    public double ElapsedMs { get; init; }
    public IReadOnlyDictionary<string, double> PhaseMs { get; init; } = new Dictionary<string, double>();
}

public enum ScanCompleteness { Complete, Partial }
public sealed record ScanIssue(string Path, string Reason);
[Flags]
public enum AccountingFeatures { None = 0, UnnamedData = 1, NamedStreams = 2, DirectoryMetadata = 4, FileIdentity = 8 }

public sealed class ScanResult
{
    public required VolumeInfo Volume { get; init; }
    public required FsNode Root { get; init; }
    public required ScanStats Stats { get; init; }
    public string EngineName { get; init; } = "";
    public string RequestedEngine { get; init; } = "auto";
    public string? FallbackReason { get; init; }
    public ScanCompleteness Completeness { get; init; }
    public IReadOnlyList<ScanIssue> Issues { get; init; } = Array.Empty<ScanIssue>();
    public AccountingFeatures AccountingFeatures { get; init; } = AccountingFeatures.UnnamedData;
    public bool IsWholeVolume => string.Equals(Path.GetPathRoot(Volume.RootPath), Volume.RootPath, StringComparison.OrdinalIgnoreCase);
    public ulong UnaccountedBytes => IsWholeVolume && Volume.TotalBytes >= Volume.FreeBytes
        && Volume.TotalBytes - Volume.FreeBytes > Root.TotalAllocated
        ? Volume.TotalBytes - Volume.FreeBytes - Root.TotalAllocated : 0;
}
