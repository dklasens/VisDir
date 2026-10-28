using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VisDir.App;
using VisDir.App.Scan;
using VisDir.App.Sunburst;

namespace VisDir.Core.Tests;

public sealed class WpfViewTests
{
    [Fact]
    public Task FrozenResults_CanBeExpandedFilteredAndReleasedByTheRealWindow() => OnSta(async () =>
    {
        var root = new FsNode { Name = @"C:\fixture", Flags = NodeFlags.Directory };
        for (int i = 0; i < 1250; i++)
            root.AddChild(new FsNode { Name = $"item-{i:D4}.bin", AllocatedSize = 8192 });
        TreeOps.Finalize(root);
        TreeOps.Freeze(root);
        var result = new ScanResult
        {
            Root = root, Volume = new VolumeInfo { RootPath = root.Name, DisplayName = "Fixture" },
            Stats = new ScanStats { FileCount = 1250 }, EngineName = "generic",
        };
        using var service = new ScanService((_, _, _, _) => Task.FromResult(result));
        var window = new MainWindow(service);
        try
        {
            Assert.Equal(1, ((ProgressBar)window.FindName("ScanProgressBar")).Maximum);
            Invoke(window, "StartScan", root.Name, "auto");
            await WaitUntil(() => !service.IsScanning);
            Assert.Same(root, ((SunburstControl)window.FindName("Burst")).ViewRoot);
            var list = (ListBox)window.FindName("ChildrenList");
            Assert.Contains(list.Items.OfType<FileItemView>(), row => row.IsAggregated);
            Invoke(window, "OnShowAllItemsClick", window, new RoutedEventArgs());
            await Field<Task>(window, "_listBuildTask");
            Assert.Equal(DirectoryPage.PageSize, list.Items.Count);
            Assert.Contains(1250.ToString("N0"), ((TextBlock)window.FindName("ListResultCount")).Text);
            Invoke(window, "OnNextPageClick", window, new RoutedEventArgs());
            await Field<Task>(window, "_listBuildTask");
            Assert.Equal("item-0200.bin", ((FileItemView)list.Items[0]).Name);
            ((TextBox)window.FindName("SearchBox")).Text = "item-12";
            Invoke(window, "RebuildFileList");
            await Field<Task>(window, "_listBuildTask");
            Assert.Equal(50, list.Items.Count);
            list.SelectedIndex = 0;
            Assert.Equal(Visibility.Visible, ((Border)window.FindName("SelectedInfoBorder")).Visibility);
            Invoke(window, "ShowLandingView");
            Assert.Null(Field<FsNode?>(window, "_viewRoot"));
            Assert.Null(Field<DirectoryViewSession>(window, "_viewSession").Root);
            Assert.Null(((SunburstControl)window.FindName("Burst")).ViewRoot);
            Assert.Empty(list.Items);
        }
        finally { window.Close(); }
    });

    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Invoke(MainWindow window, string name, params object[] arguments) =>
        typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, arguments);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "The view did not finish publishing its scan result.");
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
