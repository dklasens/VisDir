using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Concurrent;
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
    private int _scanSequence;
    private int _activeScanId;
    private readonly ConcurrentDictionary<int, byte> _cancelledScans = new();

    public event Action<double>? ProgressChanged;          // 0..1 estimate
    public event Action<string>? StatusChanged;
    public event Action<ScanResult>? Completed;
    public event Action<string>? Failed;
    public event Action? Cancelled;

    public static bool WorkerAvailable =>
        !string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath);

    public bool IsScanning => _process is { HasExited: false };

    public void Start(string path, string mode = "auto")
    {
        if (IsScanning) return;
        if (!WorkerAvailable)
        {
            Failed?.Invoke("The VisDir scanner worker is missing from the application directory.");
            return;
        }

        _process?.Dispose();
        _process = null;
        int scanId = Interlocked.Increment(ref _scanSequence);
        Volatile.Write(ref _activeScanId, scanId);

        string exe = Environment.ProcessPath!;
        string tempFile = Path.Combine(Path.GetTempPath(), $"visdir_{Guid.NewGuid():N}.vdir");
        _tempFile = tempFile;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("--worker");
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(tempFile);
        psi.ArgumentList.Add("--top");
        psi.ArgumentList.Add("0");

        var sw = Stopwatch.StartNew();
        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _activeScanId, 0);
            TryDeleteTempFile(tempFile);
            Failed?.Invoke($"Failed to launch scanner: {ex.Message}");
            return;
        }
        if (_process is null)
        {
            Volatile.Write(ref _activeScanId, 0);
            TryDeleteTempFile(tempFile);
            Failed?.Invoke("Scanner failed to start.");
            return;
        }

        StatusChanged?.Invoke($"Scanning {path}…");

        Process process = _process;
        string? workerError = null;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (e.Data.StartsWith("ERROR:", StringComparison.Ordinal))
                Interlocked.Exchange(ref workerError, e.Data[6..].Trim());
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

        process.Exited += (_, _) =>
        {
            try
            {
                bool wasCancelled = _cancelledScans.ContainsKey(scanId);
                if (!wasCancelled && process.ExitCode == 0 && File.Exists(tempFile))
                {
                    using var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                        FileOptions.DeleteOnClose);
                    ScanResult result = TreeSerializer.Read(fs);
                    Completed?.Invoke(result);
                }
                else if (wasCancelled)
                {
                    // Deliberate cancellation: a calm, expected outcome — not an error.
                    Cancelled?.Invoke();
                }
                else
                {
                    string? detail = Volatile.Read(ref workerError);
                    Failed?.Invoke(string.IsNullOrWhiteSpace(detail)
                        ? $"Scanner exited with code {process.ExitCode}."
                        : $"Scanner failed: {detail}");
                }
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex.Message);
            }
            finally
            {
                _cancelledScans.TryRemove(scanId, out _);
                TryDeleteTempFile(tempFile);
                if (Volatile.Read(ref _activeScanId) == scanId)
                    Volatile.Write(ref _activeScanId, 0);
            }
        };

        // Attach the handler before enabling events so an immediately-exiting worker cannot be missed.
        process.EnableRaisingEvents = true;
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
    }

    public void Cancel()
    {
        int scanId = Volatile.Read(ref _activeScanId);
        if (scanId != 0) _cancelledScans.TryAdd(scanId, 0);
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

    private static void TryDeleteTempFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
