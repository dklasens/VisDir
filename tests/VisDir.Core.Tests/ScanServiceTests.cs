using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using VisDir.App.Scan;
using VisDir.Scanner;

namespace VisDir.Core.Tests;

public sealed class ScanServiceTests
{
    [Fact]
    public async Task BusyIncludesLoadingAndFirstLayout_UntilMatchingResultIsAccepted()
    {
        var finish = Signal<ScanResult>();
        var started = Signal<CancellationToken>();
        var completed = Signal<ScanResult>();
        int calls = 0, measurements = 0;
        using var service = new ScanService((_, _, token, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult(token);
            return finish.Task;
        });
        service.Completed += result => completed.TrySetResult(result);
        service.DisplayMeasured += _ => measurements++;
        service.Start("C:\\");
        Task work = service.CurrentWork;
        CancellationToken token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int id = service.ScanId;
        service.AcceptResult(id); // A caller cannot end an operation that has not returned a result.
        Assert.True(service.IsScanning);
        finish.SetResult(Result());
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.IsScanning);
        service.Start("D:\\");
        Assert.Equal(1, calls);
        service.AcceptResult(id + 1);
        Assert.True(service.IsScanning);
        service.AcceptResult(id);
        Assert.False(service.IsScanning);
        service.AcceptResult(id);
        Assert.Equal(1, measurements);
        Assert.Throws<ObjectDisposedException>(() => _ = token.WaitHandle);
    }

    [Fact]
    public async Task CancelledScanCannotPublishProgressFailureOrResultIntoNextScan()
    {
        var first = Signal<ScanResult>();
        var second = Signal<ScanResult>();
        var started = Signal<(CancellationToken Token, IProgress<ScanProgress> Progress)>();
        var completed = new ConcurrentQueue<ScanResult>();
        int failures = 0, cancellations = 0, progressCount = 0;
        using var service = new ScanService((options, _, token, progress) =>
        {
            if (options.Path == "first")
            {
                started.SetResult((token, progress));
                return first.Task; // Deliberately ignores cancellation, simulating late I/O.
            }
            return second.Task;
        });
        service.Completed += completed.Enqueue;
        service.Failed += _ => failures++;
        service.Cancelled += () => cancellations++;
        service.ProgressChanged += _ => progressCount++;
        service.Start("first");
        Task oldWork = service.CurrentWork;
        var old = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int oldId = service.ScanId;
        service.Cancel();
        service.Start("second");
        Task newWork = service.CurrentWork;
        int newId = service.ScanId;
        old.Progress.Report(new ScanProgress(100, 1, 100, 1));
        first.SetException(new IOException("late old failure"));
        await oldWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(completed);
        Assert.Equal(0, failures);
        Assert.Equal(0, progressCount);
        Assert.Equal(1, cancellations);
        Assert.False(service.IsCurrent(oldId));
        Assert.True(service.IsCurrent(newId));
        Assert.True(service.IsScanning);
        Assert.Throws<ObjectDisposedException>(() => _ = old.Token.WaitHandle);
        second.SetResult(Result());
        await newWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(completed);
        service.AcceptResult(newId);
        Assert.False(service.IsScanning);
    }

    [Fact]
    public async Task CancelDuringFirstLayoutInvalidatesResultAndReleasesSession()
    {
        CancellationToken captured = default;
        using var service = new ScanService((_, _, token, _) =>
        {
            captured = token;
            return Task.FromResult(Result());
        });
        service.Start("C:\\");
        int id = service.ScanId;
        await service.CurrentWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.IsScanning);
        service.Cancel();
        service.AcceptResult(id);
        Assert.False(service.IsScanning);
        Assert.False(service.IsCurrent(id));
        Assert.Throws<ObjectDisposedException>(() => _ = captured.WaitHandle);
    }

    [Fact]
    public async Task FailureReleasesSessionAndAllowsAnotherScan()
    {
        int attempts = 0, failures = 0;
        using var service = new ScanService((_, _, _, _) => ++attempts == 1
            ? Task.FromException<ScanResult>(new IOException("unavailable"))
            : Task.FromResult(Result()));
        service.Failed += _ => failures++;
        service.Start("first");
        await service.CurrentWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.IsScanning);
        Assert.Equal(1, failures);
        service.Start("second");
        await service.CurrentWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.IsScanning);
        service.AcceptResult(service.ScanId);
    }

    [Fact]
    public async Task PipeAclIsRestrictedToCurrentSid_AndWorkerChecksServerProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        string name = "visdir-" + Guid.NewGuid().ToString("N");
        using var server = ScanService.CreateWorkerPipe(name);
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        AuthorizationRuleCollection rules = server.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        Assert.NotEmpty(rules.Cast<PipeAccessRule>());
        Assert.All(rules.Cast<PipeAccessRule>(), rule => Assert.Equal(user.User, rule.IdentityReference));
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await connection.WaitAsync(TimeSpan.FromSeconds(5));
        WorkerHost.VerifyServer(client, Environment.ProcessId);
        Assert.Throws<UnauthorizedAccessException>(() => WorkerHost.VerifyServer(client, int.MaxValue));
    }

    [Fact]
    public async Task ActualWorkerTransfersSnapshotAndExitsWithoutOpeningWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        string fixture = NewFixture();
        int pid = 0;
        try
        {
            ScanResult result = await ScanService.RunWorkerAsync(new ScanOptions { Path = fixture }, "generic",
                CancellationToken.None, new CallbackProgress(_ => { }), BuiltApplication(),
                workerStarted: value => pid = value).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("generic", result.EngineName);
            Assert.Equal(1, result.Stats.FileCount);
            Assert.True(result.Root.IsFrozen);
            Assert.True(result.Root.TotalAllocated >= 8192);
            Assert.True(result.Stats.PhaseMs.ContainsKey("broker-startup"));
            Assert.True(result.Stats.PhaseMs.ContainsKey("snapshot-load"));
            AssertExited(pid);
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("loading")]
    [InlineData("timeout")]
    [InlineData("error")]
    public async Task ActualWorkerTerminatesOnCancellationTimeoutAndError(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        string fixture = NewFixture();
        int pid = 0;
        using var cancellation = new CancellationTokenSource();
        try
        {
            Task<ScanResult> scan = ScanService.RunWorkerAsync(new ScanOptions
                { Path = scenario == "error" ? Path.Combine(fixture, "missing") : fixture },
                "generic", cancellation.Token,
                new CallbackProgress(value => { if (scenario == "loading" && value.Phase == "Loading results") cancellation.Cancel(); }), BuiltApplication(),
                connectionTimeout: scenario == "timeout" ? TimeSpan.Zero : null,
                workerStarted: value => { pid = value; if (scenario == "cancel") cancellation.Cancel(); });
            if (scenario is "cancel" or "loading") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
            else if (scenario == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => scan);
            else await Assert.ThrowsAsync<IOException>(() => scan);
            AssertExited(pid);
        }
        finally { Directory.Delete(fixture, true); }
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ScanResult Result() => new()
    {
        Root = new FsNode { Name = "C:\\", Flags = NodeFlags.Directory },
        Volume = new VolumeInfo { RootPath = "C:\\", DisplayName = "Test" }, Stats = new ScanStats(), EngineName = "generic",
    };

    private static string NewFixture()
    {
        string path = Path.Combine(Path.GetTempPath(), "visdir-broker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[8192]);
        return path;
    }

    private static string BuiltApplication()
    {
        string local = Path.Combine(Path.GetDirectoryName(typeof(VisDir.App.App).Assembly.Location)!, "VisDir.App.exe");
        if (File.Exists(local)) return local;
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "VisDir.sln"))) continue;
            string configuration = AppContext.BaseDirectory.Contains("\\Debug\\", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
            return Path.Combine(dir.FullName, "src", "VisDir.App", "bin", configuration, "net10.0-windows10.0.19041.0", "VisDir.App.exe");
        }
        throw new FileNotFoundException("Build VisDir.App before running broker integration tests.");
    }

    private static void AssertExited(int pid)
    {
        Assert.NotEqual(0, pid);
        try { using Process worker = Process.GetProcessById(pid); Assert.True(worker.HasExited); }
        catch (ArgumentException) { } // The process has exited and Windows removed its PID.
    }

    private sealed class CallbackProgress(Action<ScanProgress> action) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => action(value);
    }
}
