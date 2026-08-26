namespace VisDir.Core;

public sealed class ScanOptions
{
    public required string Path { get; init; }
    /// <summary>Worker thread count. 0 = processor count.</summary>
    public int Threads { get; init; }
}

public interface IDiskScanner
{
    ScanResult Scan(ScanOptions options, CancellationToken cancellationToken, IProgress<ScanProgress>? progress);
}
