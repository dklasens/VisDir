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
public sealed class ScanStats
{
    public long FileCount;
    public long DirectoryCount;
    public long ErrorCount;
    public ulong BytesSeen;
    public double ElapsedMs;
}

public sealed class ScanResult
{
    public required VolumeInfo Volume { get; init; }
    public required FsNode Root { get; init; }
    public required ScanStats Stats { get; init; }
    public string EngineName { get; init; } = "";
}
