using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VisDir.App;
using VisDir.App.Scan;

namespace VisDir.Core.Tests;

public sealed class LoadingProgressTests
{
    [Fact]
    public async Task LoadingPhaseAndEndpoints_BypassTheScanProgressThrottle()
    {
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fractions = new ConcurrentQueue<double>();
        var statuses = new ConcurrentQueue<string>();
        using var service = new ScanService((_, _, _, progress) =>
        {
            progress.Report(new ScanProgress(42, 4, 100, 1, -1, "Scanning"));
            progress.Report(new ScanProgress(42, 4, 100, 1, -1, "Loading results"));
            progress.Report(new ScanProgress(42, 4, 100, 1, 0, "Loading results"));
            progress.Report(new ScanProgress(42, 4, 100, 1, 1, "Loading results"));
            reported.SetResult();
            return finish.Task;
        });
        service.ProgressChanged += fractions.Enqueue;
        service.StatusChanged += statuses.Enqueue;
        try
        {
            service.Start("fixture", "generic");
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { -1d, -1d, 0d, 1d }, fractions);
            string[] loading = statuses.Where(s => s.StartsWith("Loading results", StringComparison.Ordinal)).ToArray();
            Assert.Equal(3, loading.Length);
            Assert.Contains($"({0d:P0})", loading[1]);
            Assert.Contains($"({1d:P0})", loading[2]);
            Assert.All(loading, text =>
            {
                Assert.Contains("42 files", text);
                Assert.Contains("4 folders", text);
            });
        }
        finally
        {
            finish.TrySetResult(Result());
            await service.CurrentWork.WaitAsync(TimeSpan.FromSeconds(5));
            service.AcceptResult(service.ScanId);
        }
    }

    [Fact]
    public Task Window_DisplaysLoadingZeroAndOneAsKnownProgressAndRemainsResponsive() => OnSta(async () =>
    {
        var started = new TaskCompletionSource<IProgress<ScanProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ScanService((_, _, _, progress) =>
        {
            started.SetResult(progress);
            return finish.Task;
        });
        var window = new MainWindow(service);
        try
        {
            typeof(MainWindow).GetMethod("StartScan", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, ["fixture", "generic"]);
            IProgress<ScanProgress> progress = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var bar = (ProgressBar)window.FindName("ScanProgressBar");
            var phase = (TextBlock)window.FindName("ScanPhaseText");
            progress.Report(new ScanProgress(42, 4, 100, 1, -1, "Scanning"));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.True(bar.IsIndeterminate);

            // Both endpoints follow immediately, exercising the former second throttle.
            progress.Report(new ScanProgress(42, 4, 100, 1, 0, "Loading results"));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.False(bar.IsIndeterminate);
            Assert.Equal(0, bar.Value);
            Assert.Contains($"Loading results ({0d:P0})", phase.Text);
            Assert.Contains("42 files", phase.Text);

            progress.Report(new ScanProgress(42, 4, 100, 1, 1, "Loading results"));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.False(bar.IsIndeterminate);
            Assert.Equal(1, bar.Value);
            Assert.Contains($"Loading results ({1d:P0})", phase.Text);

            finish.SetResult(Result());
            for (int i = 0; i < 500 && service.IsScanning; i++) await Task.Delay(10);
            Assert.False(service.IsScanning);
            Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("ScanOverlay")).Visibility);
        }
        finally
        {
            finish.TrySetResult(Result());
            window.Close();
        }
    });

    private static ScanResult Result()
    {
        var root = new FsNode { Name = @"C:\fixture", Flags = NodeFlags.Directory };
        TreeOps.Freeze(root);
        return new ScanResult
        {
            Root = root, Volume = new VolumeInfo { RootPath = root.Name, DisplayName = "Fixture" },
            Stats = new ScanStats(), EngineName = "generic",
        };
    }

    private static Task OnSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
