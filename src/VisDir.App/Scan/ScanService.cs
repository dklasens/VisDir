using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using VisDir.Core;
using VisDir.Scanner;

namespace VisDir.App.Scan;

/// <summary>Owns one complete scan/load/publish session. Only the scanner broker may request elevation.</summary>
public sealed class ScanService : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<ScanOptions, string, CancellationToken, IProgress<ScanProgress>, Task<ScanResult>> _run;
    private Session? _session;
    private int _generation;
    private bool _busy, _disposed;

    private sealed class Session(int id)
    {
        public int Id { get; } = id;
        public CancellationTokenSource Cancellation { get; } = new();
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public double LoadCompleteMs;
        public long LastProgressMs;
        public string LastProgressPhase = "";
        public bool LastProgressWasDeterminate;
        public Task? Work;
        public bool ResultReady;
        private int _disposed;
        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Cancellation.Dispose();
        }
    }

    public event Action<double>? ProgressChanged;
    public event Action<string>? StatusChanged;
    public event Action<ScanResult>? Completed;
    public event Action<string>? Failed;
    public event Action? Cancelled;
    public event Action<ScanDisplayMetrics>? DisplayMeasured;
    public static bool WorkerAvailable => !string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath);
    public int ScanId { get { lock (_gate) return _generation; } }
    public bool IsScanning { get { lock (_gate) return _busy; } }
    public bool IsCurrent(int id) { lock (_gate) return !_disposed && _generation == id; }
    internal Task CurrentWork { get { lock (_gate) return _session?.Work ?? Task.CompletedTask; } }

    public ScanService() : this(RunWorkerAsync) { }
    internal ScanService(Func<ScanOptions, string, CancellationToken, IProgress<ScanProgress>, Task<ScanResult>> run) => _run = run;

    public void Start(string path, string mode = "auto")
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_busy) return;
            if (_session is { } previous) Retire(previous, cancel: false);
            var session = new Session(++_generation);
            _session = session;
            _busy = true;
            StatusChanged?.Invoke($"Scanning {path}…");
            session.Work = Task.Run(() => RunAsync(session, path, mode));
        }
    }

    private async Task RunAsync(Session session, string path, string mode)
    {
        bool awaitingPublication = false;
        try
        {
            var progress = new CallbackProgress(value =>
            {
                lock (_gate)
                {
                    if (_disposed || _generation != session.Id || session.Cancellation.IsCancellationRequested) return;
                    long now = Environment.TickCount64;
                    bool determinate = double.IsFinite(value.Fraction) && value.Fraction is >= 0 and <= 1;
                    bool phaseChanged = !string.Equals(session.LastProgressPhase, value.Phase, StringComparison.Ordinal);
                    if (!phaseChanged && determinate == session.LastProgressWasDeterminate && value.Fraction != 1 &&
                        now - session.LastProgressMs < 100) return;
                    session.LastProgressMs = now;
                    session.LastProgressPhase = value.Phase;
                    session.LastProgressWasDeterminate = determinate;
                    ProgressChanged?.Invoke(value.Fraction);
                    string phase = value.Phase == "Loading results" && determinate
                        ? $"Loading results ({value.Fraction:P0})" : value.Phase;
                    StatusChanged?.Invoke(value.FilesSeen == 0 && value.DirsSeen == 0 ? phase
                        : $"{phase} · {value.FilesSeen:N0} files · {value.DirsSeen:N0} folders");
                }
            });
            ScanResult result = await _run(new ScanOptions { Path = path }, mode, session.Cancellation.Token, progress).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || _generation != session.Id || session.Cancellation.IsCancellationRequested) return;
                session.LoadCompleteMs = session.Clock.Elapsed.TotalMilliseconds;
                session.ResultReady = true;
                awaitingPublication = true;
                StatusChanged?.Invoke("Building visualization…");
                // Busy remains true through the UI's first layout; AcceptResult ends it.
                Completed?.Invoke(result);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (_disposed || _generation != session.Id) return;
                _busy = false;
                _session = null;
                awaitingPublication = false;
                Cancelled?.Invoke();
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_disposed || _generation != session.Id) return;
                _busy = false;
                _session = null;
                awaitingPublication = false;
                Failed?.Invoke(ex.Message);
            }
        }
        finally
        {
            if (!awaitingPublication) session.DisposeCancellation();
        }
    }

    public void AcceptResult(int id)
    {
        lock (_gate)
        {
            if (_disposed || _generation != id || _session is not { ResultReady: true } session) return;
            _busy = false;
            _session = null;
            Retire(session, cancel: false);
            using Process current = Process.GetCurrentProcess();
            var metrics = new ScanDisplayMetrics(session.LoadCompleteMs,
                Math.Max(0, session.Clock.Elapsed.TotalMilliseconds - session.LoadCompleteMs),
                session.Clock.Elapsed.TotalMilliseconds, current.PeakWorkingSet64);
            App.SafeLog($"[Scan] load={metrics.ScanAndLoadMs:F1}ms view={metrics.FirstViewMs:F1}ms usable={metrics.FirstUsableMs:F1}ms processPeak={metrics.ProcessPeakWorkingSetBytes}");
            DisplayMeasured?.Invoke(metrics);
        }
    }

    public void Cancel()
    {
        Session? old;
        lock (_gate)
        {
            old = _session;
            bool wasBusy = _busy;
            _session = null;
            _busy = false;
            ++_generation;
            if (wasBusy && !_disposed) Cancelled?.Invoke();
        }
        if (old is not null) Retire(old, cancel: true);
    }

    private static void Retire(Session session, bool cancel)
    {
        if (cancel)
        {
            try { session.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        _ = (session.Work ?? Task.CompletedTask).ContinueWith(_ => session.DisposeCancellation(),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Cancel();
    }

    private static Task<ScanResult> RunWorkerAsync(ScanOptions options, string mode,
        CancellationToken token, IProgress<ScanProgress> progress)
        => RunWorkerAsync(options, mode, token, progress, Environment.ProcessPath!);

    internal static NamedPipeServerStream CreateWorkerPipe(string pipeName)
    {
        // CurrentUserOnly also compares integrity/elevation on Windows, which rejects
        // our elevated child. The protected SID ACL admits this user at either level.
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new IOException("The current user has no Windows identity.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(sid);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 64 * 1024, 64 * 1024, security);
    }

    internal static async Task<ScanResult> RunWorkerAsync(ScanOptions options, string mode,
        CancellationToken token, IProgress<ScanProgress> progress, string executablePath,
        TimeSpan? connectionTimeout = null, Action<int>? workerStarted = null)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) throw new IOException("The scanner executable is unavailable.");
        var transportClock = Stopwatch.StartNew();
        string pipeName = "visdir-" + Guid.NewGuid().ToString("N");
        using var pipe = CreateWorkerPipe(pipeName);
        bool elevate = new ScanCoordinator().ShouldRequestElevation(mode, options.Path);
        var start = new ProcessStartInfo
        {
            FileName = executablePath, UseShellExecute = elevate, CreateNoWindow = !elevate,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevate) start.Verb = "runas";
        start.ArgumentList.Add("--worker-pipe");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process? worker;
        try { worker = Process.Start(start); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Administrator scan cancelled.", ex, token); }
        using (worker)
        {
            if (worker is null) throw new IOException("The scanner could not be started.");
            workerStarted?.Invoke(worker.Id);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (connectionTimeout == TimeSpan.Zero) timeout.Cancel();
            else timeout.CancelAfter(connectionTimeout ?? TimeSpan.FromMinutes(2));
            using var registration = token.Register(() =>
            {
                try { pipe.Dispose(); } catch { }
                try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); } catch { }
            });
            try
            {
                Task exited = worker.WaitForExitAsync();
                while (true)
                {
                    Task connection = pipe.WaitForConnectionAsync(timeout.Token);
                    if (await Task.WhenAny(connection, exited).ConfigureAwait(false) == exited && !pipe.IsConnected)
                        throw new IOException($"Scanner exited before connecting (code {worker.ExitCode}).");
                    await connection.ConfigureAwait(false);
                    if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid) && clientPid == worker.Id) break;
                    pipe.Disconnect();
                }
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                double startupMs = transportClock.Elapsed.TotalMilliseconds;
                using var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
                writer.Write(JsonSerializer.Serialize(new WorkerRequest(options.Path, mode, options.Threads)));
                writer.Flush();
                return ReadWorkerResponse(pipe, token, progress, startupMs);
            }
            catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            { throw new TimeoutException("The scanner did not connect in time."); }
            finally
            {
                // Closing the authenticated session cancels an elevated helper even when Kill is denied.
                pipe.Dispose();
                try
                {
                    if (!worker.HasExited)
                    {
                        try { worker.Kill(entireProcessTree: true); } catch (Win32Exception) { }
                        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException) { }
            }
        }
    }

    /// <summary>Reads one complete worker response without closing the caller's stream.</summary>
    internal static ScanResult ReadWorkerResponse(Stream stream, CancellationToken token,
        IProgress<ScanProgress> progress, double startupMs)
    {
        // Buffer from the first marker onward. Replacing a buffer after the Result marker
        // would discard snapshot bytes that were read ahead with earlier messages.
        // BufferedStream has no leaveOpen option; only its reader is disposed here.
        var buffered = new BufferedStream(stream, 1 << 18);
        using var reader = new BinaryReader(buffered, Encoding.UTF8, leaveOpen: true);
        ScanProgress? lastProgress = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            byte kind = reader.ReadByte();
            if (kind == WorkerHost.Progress)
            {
                ScanProgress? value = JsonSerializer.Deserialize<ScanProgress>(reader.ReadString());
                if (value is not null)
                {
                    lastProgress = value;
                    progress.Report(value);
                }
            }
            else if (kind == WorkerHost.Result)
            {
                if (lastProgress is { } previous && (previous.FilesSeen > 0 || previous.DirsSeen > 0))
                    progress.Report(previous with { Fraction = -1, Phase = "Loading results" });
                var loadingClock = Stopwatch.StartNew();
                ScanResult result = TreeSerializer.Read(buffered, recomputeTotals: false, token, progress);
                var phases = new Dictionary<string, double>(result.Stats.PhaseMs)
                {
                    ["broker-startup"] = startupMs,
                    ["snapshot-load"] = loadingClock.Elapsed.TotalMilliseconds,
                };
                return new ScanResult
                {
                    Root = result.Root, Volume = result.Volume, EngineName = result.EngineName,
                    RequestedEngine = result.RequestedEngine, FallbackReason = result.FallbackReason,
                    Completeness = result.Completeness, Issues = result.Issues, AccountingFeatures = result.AccountingFeatures,
                    Stats = result.Stats with { PhaseMs = new System.Collections.ObjectModel.ReadOnlyDictionary<string, double>(phases) },
                };
            }
            else if (kind == WorkerHost.Error) throw new IOException(reader.ReadString());
            else if (kind == WorkerHost.Cancelled) throw new OperationCanceledException(token);
            else throw new InvalidDataException("Unknown scanner message.");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
    private sealed class CallbackProgress(Action<ScanProgress> callback) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => callback(value);
    }
}

public sealed record ScanDisplayMetrics(double ScanAndLoadMs, double FirstViewMs, double FirstUsableMs, long ProcessPeakWorkingSetBytes);
