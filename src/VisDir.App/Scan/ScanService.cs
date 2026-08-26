using System.IO;
using System.Diagnostics;
using System.Globalization;
using VisDir.Core;

namespace VisDir.App.Scan;

/// <summary>
/// Drives the elevated-capable scanner worker process and loads its binary snapshot.
/// Progress arrives via stderr; the finished tree via a temp snapshot file.
/// </summary>
public sealed class ScanService : IDisposable
{
    private Process? _process;
    private string? _tempFile;

    public event Action<double>? ProgressChanged;          // 0..1 estimate
    public event Action<string>? StatusChanged;
    public event Action<ScanResult>? Completed;
    public event Action<string>? Failed;
    public event Action? Cancelled;

    private volatile bool _cancelRequested;

    public static bool WorkerAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Scanner", "VisDir.Scanner.exe"));

    public bool IsScanning => _process is { HasExited: false };

    public void Start(string path, string mode = "auto")
    {
        if (IsScanning) return;
        _cancelRequested = false;

        string exe = Path.Combine(AppContext.BaseDirectory, "Scanner", "VisDir.Scanner.exe");
        _tempFile = Path.Combine(Path.GetTempPath(), $"visdir_{Guid.NewGuid():N}.vdir");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(_tempFile);
        psi.ArgumentList.Add("--top");
        psi.ArgumentList.Add("0");

        var sw = Stopwatch.StartNew();
        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"Failed to launch scanner: {ex.Message}");
            return;
        }
        if (_process is null)
        {
            Failed?.Invoke("Scanner failed to start.");
            return;
        }

        StatusChanged?.Invoke($"Scanning {path}…");

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (e.Data.StartsWith("PROGRESS ", StringComparison.Ordinal))
            {
                Dictionary<string, string> values = ParseValues(e.Data);
                double fraction = ParseDouble(values, "fraction", -1);
                ProgressChanged?.Invoke(fraction);
                long files = ParseLong(values, "files");
                long dirs = ParseLong(values, "dirs");
                string phase = values.TryGetValue("phase", out string? encoded)
                    ? Uri.UnescapeDataString(encoded)
                    : "Scanning";
                StatusChanged?.Invoke($"{phase} · {files:N0} files · {dirs:N0} folders");
            }
            else if (e.Data.StartsWith("ENGINE ", StringComparison.Ordinal))
            {
                Dictionary<string, string> values = ParseValues(e.Data);
                string engine = values.GetValueOrDefault("selected", "scanner");
                string reason = Uri.UnescapeDataString(values.GetValueOrDefault("reason", ""));
                StatusChanged?.Invoke($"{reason} ({engine})");
            }
            else if (e.Data.StartsWith("DONE ", StringComparison.Ordinal))
            {
                StatusChanged?.Invoke($"Building visualization · {sw.Elapsed.TotalSeconds:0.0}s scan");
                ProgressChanged?.Invoke(0.99);
            }
        };

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            try
            {
                if (_tempFile is not null && File.Exists(_tempFile))
                {
                    using var fs = new FileStream(_tempFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                        FileOptions.DeleteOnClose);
                    ScanResult result = TreeSerializer.Read(fs);
                    Completed?.Invoke(result);
                }
                else if (_cancelRequested)
                {
                    // Deliberate cancellation: a calm, expected outcome — not an error.
                    Cancelled?.Invoke();
                }
                else
                {
                    Failed?.Invoke($"Scanner exited with code {_process.ExitCode}.");
                }
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex.Message);
            }
        };

        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();
    }

    public void Cancel()
    {
        _cancelRequested = true;
        try { if (_process is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        Cancel();
        try { if (_tempFile is not null && File.Exists(_tempFile)) File.Delete(_tempFile); }
        catch { /* ignore */ }
        _process?.Dispose();
    }

    private static Dictionary<string, string> ParseValues(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            int equals = token.IndexOf('=');
            if (equals > 0) result[token[..equals]] = token[(equals + 1)..];
        }
        return result;
    }

    private static long ParseLong(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? raw) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value : 0;

    private static double ParseDouble(IReadOnlyDictionary<string, string> values, string key, double fallback) =>
        values.TryGetValue(key, out string? raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value : fallback;
}
