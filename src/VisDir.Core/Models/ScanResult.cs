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
}

public sealed class ScanResult
{
    public required VolumeInfo Volume { get; init; }
    public required FsNode Root { get; init; }
    public required ScanStats Stats { get; init; }
    public string EngineName { get; init; } = "";
}
