using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Principal;
using VisDir.Core.Scanning;

namespace VisDir.Core;

public sealed record ScannerSelection(IDiskScanner Scanner, string Name, string Reason);

/// <summary>The shared engine policy and publication boundary for GUI, worker, and CLI scans.</summary>
public sealed class ScanCoordinator
{
    private readonly Func<string, VolumeInfo> _query;
    private readonly Func<bool> _isElevated;
    private readonly Func<string, IDiskScanner> _create;

    public ScanCoordinator() : this(VolumeQuery.Query, IsElevated, name => name == "mft" ? new NtfsMftScanner() : new GenericScanner()) { }
    internal ScanCoordinator(Func<string, VolumeInfo> query, Func<bool> isElevated, Func<string, IDiskScanner> create)
        => (_query, _isElevated, _create) = (query, isElevated, create);

    /// <summary>Normal NTFS drive scans prefer direct metadata reads through an elevated
    /// helper. Small folder scans and an explicit Compatible choice do not prompt.</summary>
    public bool ShouldRequestElevation(string mode, string path)
    {
        if (mode is not ("auto" or "generic" or "mft")) throw new ArgumentException("Unknown scanner mode.", nameof(mode));
        if (mode == "generic" || _isElevated()) return false;
        if (mode == "mft") return true;
        string normalized = PathUtils.NormalizeScanRoot(path);
        return normalized.Length == 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':'
            && string.Equals(_query(normalized).FileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase);
    }

    public ScannerSelection Select(string mode, string path)
    {
        if (mode is not ("auto" or "generic" or "mft")) throw new ArgumentException("Unknown scanner mode.", nameof(mode));
        string normalized = PathUtils.NormalizeScanRoot(path);
        bool wholeVolume = string.Equals(Path.GetPathRoot(normalized), normalized, StringComparison.OrdinalIgnoreCase);
        bool ntfs = mode != "generic" && (wholeVolume || mode == "mft")
            && string.Equals(_query(normalized).FileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase);
        bool useMft = ntfs && _isElevated();
        if (mode == "mft" && ntfs && !useMft) throw new AdminRequiredException();
        string engine = useMft ? "mft" : "generic";
        return new ScannerSelection(_create(engine), engine, useMft ? "Reading NTFS metadata" : "Reading folders");
    }

    public ScanResult Run(ScanOptions options, string mode, CancellationToken cancellationToken,
        IProgress<ScanProgress>? progress, Action<ScannerSelection>? selected = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        ScannerSelection selection = Select(mode, options.Path);
        ScanResult result;
        string? fallback = null;
        selected?.Invoke(selection);
        try
        {
            try { result = selection.Scanner.Scan(options, cancellationToken, progress); }
            catch (Exception ex) when (selection.Name == "mft" && ex is not OperationCanceledException && ex is not OutOfMemoryException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fallback = ex.Message;
                var compatible = _create("generic");
                selected?.Invoke(new ScannerSelection(compatible, "generic", "NTFS scan unavailable; reading folders"));
                try { result = compatible.Scan(options, cancellationToken, progress); }
                finally { (compatible as IDisposable)?.Dispose(); }
            }
        }
        finally { (selection.Scanner as IDisposable)?.Dispose(); }

        cancellationToken.ThrowIfCancellationRequested();
        double beforeFreeze = clock.Elapsed.TotalMilliseconds;
        TreeOps.Freeze(result.Root, cancellationToken);
        var phases = new Dictionary<string, double>(result.Stats.PhaseMs)
        {
            ["freeze"] = clock.Elapsed.TotalMilliseconds - beforeFreeze,
            ["coordinator"] = clock.Elapsed.TotalMilliseconds,
        };
        return new ScanResult
        {
            Root = result.Root,
            Volume = result.Volume,
            EngineName = result.EngineName,
            RequestedEngine = mode,
            FallbackReason = fallback,
            Completeness = result.Stats.ErrorCount > 0 ? ScanCompleteness.Partial : result.Completeness,
            Issues = Array.AsReadOnly(result.Issues.ToArray()),
            AccountingFeatures = result.AccountingFeatures,
            Stats = result.Stats with
            {
                BytesSeen = result.Root.TotalAllocated,
                ElapsedMs = clock.Elapsed.TotalMilliseconds,
                PhaseMs = new ReadOnlyDictionary<string, double>(phases)
            },
        };
    }

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
