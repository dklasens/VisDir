using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VisDir.App.Scan;
using VisDir.App.Sunburst;
using VisDir.Core;

namespace VisDir.App;

public sealed class DriveChoice
{
    public required string DisplayName { get; init; }
    public required string RootPath { get; init; }
    public required string CapacityText { get; init; }
    public required string FreeText { get; init; }
    public double UsedFraction { get; init; }
    public string UsedPercentText => $"{UsedFraction:P0}";
    public bool IsVolumeRoot { get; init; }
    public bool SupportsFastScan { get; init; }
    public ulong TotalBytes { get; init; }
    public ulong FreeBytes { get; init; }
    public override string ToString() => DisplayName;
}

public sealed class FileItemView(FsNode node, Brush chip, Brush textBrush, Brush sizeBrush, double barFraction, string toolTipText, bool isAggregated = false)
{
    public FsNode Node { get; } = node;
    public string Name => Node.Name;
    public string SizeText => SizeFormatter.Format(Node.TotalAllocated);
    public string Kind => Node.IsDirectory ? "Folder" : "File";
    public Brush ChipBrush { get; } = chip;
    public Brush TextBrush { get; } = textBrush;
    public Brush SizeBrush { get; } = sizeBrush;
    public double BarFraction { get; } = barFraction;
    public string ToolTipText { get; } = toolTipText;
    public bool IsAggregated { get; } = isAggregated;
    public override string ToString() => $"{Name}, {SizeText}, {Kind}";
}

public partial class MainWindow : Window
{
    private readonly ScanService _scanner = new();
    private readonly DispatcherTimer _driveRefreshTimer;
    private readonly List<DriveChoice> _customTargets = [];
    private List<DriveChoice> _drives = [];
    private ScanResult? _result;
    private FsNode? _viewRoot;
    private FsNode? _selectedNode;

    // Navigation History
    private readonly Stack<FsNode> _backHistory = new();
    private readonly Stack<FsNode> _forwardHistory = new();
    private bool _navigatingHistory;
    private bool _suppressFilterRebuild;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => EnableDarkTitleBar();
        RefreshDrives();

        _driveRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _driveRefreshTimer.Tick += (_, _) =>
        {
            if (!_scanner.IsScanning && LandingPanel.Visibility == Visibility.Visible)
                RefreshDrives(onlyWhenChanged: true);
        };
        _driveRefreshTimer.Start();

        _scanner.ProgressChanged += p => Dispatcher.Invoke(() =>
        {
            if (p is >= 0 and < 1)
            {
                ScanProgressBar.IsIndeterminate = false;
                ScanProgressBar.Value = p;
            }
            else
            {
                ScanProgressBar.IsIndeterminate = true;
            }
        });
        _scanner.StatusChanged += s => Dispatcher.Invoke(() => ScanPhaseText.Text = s);
        _scanner.Completed += r => Dispatcher.Invoke(() => OnScanCompleted(r));
        _scanner.Failed += msg => Dispatcher.Invoke(() =>
        {
            ScanOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, msg, "Scan Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (_result is null) ShowLandingView();
        });
        _scanner.Cancelled += () => Dispatcher.Invoke(() =>
        {
            ScanOverlay.Visibility = Visibility.Collapsed;
            if (_result is null) ShowLandingView();
        });

        Burst.HoveredChanged += node => Dispatcher.Invoke(() =>
        {
            FsNode? target = node ?? _selectedNode ?? _viewRoot;
            UpdateSelectedNodeInfo(target);
            SyncListHover(node);
        });

        Burst.NodeClicked += node => Dispatcher.Invoke(() =>
        {
            _selectedNode = node;
            UpdateSelectedNodeInfo(node);
            if (node.IsDirectory)
            {
                NavigateInto(node);
            }
            else
            {
                Burst.SelectedSource = node;
                SyncListSelection(node);
            }
        });

        Burst.CenterClicked += () => Dispatcher.Invoke(NavigateUp);

        Closed += (_, _) =>
        {
            _driveRefreshTimer.Stop();
            _scanner.Dispose();
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (SystemParameters.WorkArea.Width > 0 && SystemParameters.WorkArea.Height > 0)
        {
            Width = Math.Min(Width, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 40));
            Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 40));
        }

        Activate();
        Focus();
        Topmost = true;
        Topmost = false;

        string[] args = Environment.GetCommandLineArgs();
        int scanIndex = Array.IndexOf(args, "--scan");
        if (scanIndex < 0 || scanIndex + 1 >= args.Length) return;

        string target = args[scanIndex + 1];
        string mode = args.Contains("--fast", StringComparer.OrdinalIgnoreCase) ? "mft" : "auto";
        StartScan(target, mode);
    }

    private void EnableDarkTitleBar()
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            int enabled = 1;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            int captionColor = 0x00382622; // COLORREF for #222638
            DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
            int textColor = 0x00FFFFFF;
            DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    private void RefreshDrives(bool onlyWhenChanged = false)
    {
        string? selectedPath = (DriveCards.SelectedItem as DriveChoice)?.RootPath;
        var fresh = new List<DriveChoice>();
        foreach (DriveInfo drive in DriveInfo.GetDrives()
                     .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
                     .OrderBy(d => d.Name))
        {
            try
            {
                ulong total = (ulong)drive.TotalSize;
                ulong free = (ulong)drive.AvailableFreeSpace;
                double used = total > 0 ? (double)(total - free) / total : 0;
                string rootLabel = drive.Name.TrimEnd('\\');
                string name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? rootLabel : $"{drive.VolumeLabel} ({rootLabel})";
                fresh.Add(new DriveChoice
                {
                    DisplayName = name,
                    RootPath = drive.RootDirectory.FullName,
                    CapacityText = $"{SizeFormatter.Format(total)} capacity",
                    FreeText = $"{SizeFormatter.Format(free)} free of {SizeFormatter.Format(total)}",
                    UsedFraction = used,
                    IsVolumeRoot = true,
                    SupportsFastScan = string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
                    TotalBytes = total,
                    FreeBytes = free,
                });
            }
            catch { /* drive disappeared during refresh */ }
        }
        fresh.AddRange(_customTargets);

        string signature = string.Join('|', fresh.Select(d => $"{d.RootPath}:{d.UsedFraction:F5}"));
        string currentSignature = string.Join('|', _drives.Select(d => $"{d.RootPath}:{d.UsedFraction:F5}"));
        if (onlyWhenChanged && signature == currentSignature) return;

        _drives = fresh;
        DriveCards.ItemsSource = _drives;
        DriveChoice? selection = _drives.FirstOrDefault(d => PathsEqual(d.RootPath, selectedPath)) ?? _drives.FirstOrDefault();
        DriveCards.SelectedItem = selection;
    }

    private void ShowLandingView()
    {
        LandingPanel.Visibility = Visibility.Visible;
        ContentShell.Visibility = Visibility.Collapsed;
        EngineBadge.Visibility = Visibility.Collapsed;
        RescanButton.Visibility = Visibility.Collapsed;
        BreadcrumbBar.Children.Clear();
        DisksRootButton.IsEnabled = false;
        RefreshDrives();
    }

    private void OnDisksRootClick(object sender, RoutedEventArgs e)
    {
        ShowLandingView();
    }

    private void OnDriveCardSelected(object sender, SelectionChangedEventArgs e) { }

    private void OnDriveCardDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DriveCards.SelectedItem is DriveChoice choice)
        {
            string mode = (LandingScanModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
            StartScan(choice.RootPath, mode);
        }
    }

    private void OnDriveCardScanClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DriveChoice choice })
        {
            string mode = (LandingScanModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
            StartScan(choice.RootPath, mode);
        }
    }

    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to scan", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        StartScan(dialog.FolderName, "generic");
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files is { Length: > 0 } && Directory.Exists(files[0]))
            {
                StartScan(files[0], "generic");
                e.Handled = true;
            }
        }
    }

    private void StartScan(string path, string mode = "auto")
    {
        if (_scanner.IsScanning) return;

        bool isDriveRoot = Path.GetPathRoot(path)?.Equals(path, StringComparison.OrdinalIgnoreCase) ?? false;
        if (mode == "mft" && !isDriveRoot)
        {
            mode = "generic";
        }
        if (mode == "mft" && !IsElevated())
        {
            RequestElevatedRestart(path);
            return;
        }

        LandingPanel.Visibility = Visibility.Collapsed;
        ContentShell.Visibility = Visibility.Visible;
        ScanOverlay.Visibility = Visibility.Visible;
        ScanOverlayTarget.Text = path;
        ScanProgressBar.IsIndeterminate = true;
        ScanPhaseText.Text = "Initializing scan…";
        DisksRootButton.IsEnabled = true;

        BreadcrumbBar.Children.Clear();
        Burst.ViewRoot = null;
        _result = null;
        _viewRoot = null;
        _selectedNode = null;
        _backHistory.Clear();
        _forwardHistory.Clear();
        UpdateHistoryButtons();

        ChildrenList.ItemsSource = null;
        CurrentFolderName.Text = Path.GetFileName(path) is { Length: > 0 } fn ? fn : path;
        CurrentFolderTotalSize.Text = "Scanning…";
        UpdateSelectedNodeInfo(null);

        _scanner.Start(path, mode);
    }

    private void OnCancelScanClick(object sender, RoutedEventArgs e)
    {
        if (_scanner.IsScanning)
        {
            ScanPhaseText.Text = "Cancelling scan…";
            _scanner.Cancel();
        }
    }

    private void OnScanCompleted(ScanResult result)
    {
        _result = result;
        ScanOverlay.Visibility = Visibility.Collapsed;

        EngineBadge.Visibility = Visibility.Visible;
        EngineBadgeText.Text = result.EngineName?.Trim().ToLowerInvariant() switch
        {
            "mft" => "FAST NTFS",
            "generic" or "compatible" => "COMPATIBLE",
            { Length: > 0 } other => other.ToUpperInvariant(),
            _ => "SCANNED",
        };
        RescanButton.Visibility = Visibility.Visible;

        Burst.Volume = result.Volume;
        _backHistory.Clear();
        _forwardHistory.Clear();
        UpdateHistoryButtons();
        NavigateInto(result.Root, recordHistory: false);
    }

    private void NavigateInto(FsNode node, bool recordHistory = true)
    {
        if (!node.IsDirectory) return;

        if (recordHistory && _viewRoot is not null && !ReferenceEquals(_viewRoot, node) && !_navigatingHistory)
        {
            _backHistory.Push(_viewRoot);
            _forwardHistory.Clear();
            UpdateHistoryButtons();
        }

        ClearSearchFilter();
        _viewRoot = node;
        _selectedNode = node;
        Burst.ViewRoot = node;

        CurrentFolderName.Text = node.Name.TrimEnd('\\');
        if (CurrentFolderName.Text.Length == 0) CurrentFolderName.Text = node.Name;
        CurrentFolderTotalSize.Text = SizeFormatter.Format(node.TotalAllocated);

        RebuildBreadcrumbs();
        RebuildFileList();
        UpdateSelectedNodeInfo(node);
        UpdateHistoryButtons();
    }

    private void NavigateUp()
    {
        if (_viewRoot?.Parent is { } parent)
        {
            NavigateInto(parent);
        }
        else
        {
            ShowLandingView();
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_backHistory.Count == 0 || _viewRoot is null) return;
        _navigatingHistory = true;
        _forwardHistory.Push(_viewRoot);
        FsNode prev = _backHistory.Pop();
        NavigateInto(prev, recordHistory: false);
        _navigatingHistory = false;
        UpdateHistoryButtons();
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (_forwardHistory.Count == 0 || _viewRoot is null) return;
        _navigatingHistory = true;
        _backHistory.Push(_viewRoot);
        FsNode next = _forwardHistory.Pop();
        NavigateInto(next, recordHistory: false);
        _navigatingHistory = false;
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        BackButton.IsEnabled = _backHistory.Count > 0;
        ForwardButton.IsEnabled = _forwardHistory.Count > 0;
    }

    private void RebuildBreadcrumbs()
    {
        BreadcrumbBar.Children.Clear();
        if (_viewRoot is null) return;

        var chain = new List<FsNode>();
        for (FsNode? n = _viewRoot; n is not null; n = n.Parent) chain.Add(n);
        chain.Reverse();

        foreach (FsNode node in chain)
        {
            bool current = ReferenceEquals(node, _viewRoot);
            BreadcrumbBar.Children.Add(new TextBlock
            {
                Text = "›",
                Foreground = (Brush)FindResource("DimBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                FontSize = 13,
            });

            var button = new Button
            {
                Content = node.Name.TrimEnd('\\'),
                Tag = node,
                IsEnabled = !current,
                Style = (Style)FindResource("BreadcrumbButton"),
                Foreground = current ? Brushes.White : (Brush)FindResource("DimBrush"),
                FontSize = 12,
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            };
            button.Click += (_, _) => NavigateInto((FsNode)button.Tag);
            BreadcrumbBar.Children.Add(button);
        }
    }

    private void RebuildFileList()
    {
        if (_viewRoot is null) return;
        string query = SearchBox.Text.Trim();
        IReadOnlyList<FsNode> allChildren = _viewRoot.Children ?? [];
        IEnumerable<FsNode> children = allChildren;
        if (query.Length > 0) children = children.Where(n => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        List<FsNode> nodes = children.ToList();

        // Calculate branch ranks to match wedge colors exactly
        var rankOf = new Dictionary<FsNode, int>(ReferenceEqualityComparer.Instance);
        int branchCount = 0;
        const double minSweep = 0.006;
        ulong viewTotal = _viewRoot.TotalAllocated;
        for (int i = 0; i < allChildren.Count; i++)
        {
            FsNode child = allChildren[i];
            bool isVisibleWedge = viewTotal == 0 || i == 0 ||
                SunburstLayout.FullCircle * child.TotalAllocated / viewTotal >= minSweep;
            if (isVisibleWedge) rankOf[child] = branchCount++;
        }

        // Set active folder dot color
        CurrentFolderDot.Background = Palette.BrushForBranch(0, Math.Max(branchCount, 1), 1);

        ulong largest = nodes.Count > 0 ? nodes.Max(n => n.TotalAllocated) : 0;
        var items = new List<FileItemView>(nodes.Count);
        ulong aggregatedBytes = 0;
        int aggregatedCount = 0;

        var normalTextBrush = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        var dimTextBrush = TryFindResource("DimBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x8E, 0x95, 0xAA));

        foreach (FsNode node in nodes)
        {
            bool isVisible = rankOf.ContainsKey(node);
            if (!isVisible && query.Length == 0)
            {
                aggregatedBytes += node.TotalAllocated;
                aggregatedCount++;
                continue;
            }

            double fraction = viewTotal > 0 ? (double)node.TotalAllocated / viewTotal : 0;
            string tip = $"{node.Name}\n{SizeFormatter.Format(node.TotalAllocated)} on disk · {(node.IsDirectory ? "Folder" : "File")} · {fraction:P1} of this folder";

            items.Add(new FileItemView(
                node,
                ChipBrushFor(node, rankOf, branchCount),
                normalTextBrush,
                normalTextBrush,
                largest > 0 ? (double)node.TotalAllocated / largest : 0,
                tip));
        }

        // DaisyDisk aggregated row: "smaller objects..."
        if (aggregatedCount > 0 && query.Length == 0)
        {
            var aggNode = new FsNode
            {
                Name = "smaller objects...",
                TotalAllocated = aggregatedBytes,
                Flags = NodeFlags.Directory,
            };
            string aggTip = $"{aggregatedCount} smaller items totaling {SizeFormatter.Format(aggregatedBytes)}";
            items.Add(new FileItemView(
                aggNode,
                Palette.AggregatedBrush,
                dimTextBrush,
                dimTextBrush,
                largest > 0 ? (double)aggregatedBytes / largest : 0,
                aggTip,
                isAggregated: true));
        }

        ChildrenList.ItemsSource = items;
        if (items.Count == 0)
        {
            EmptyListText.Text = query.Length == 0 ? "This folder is empty" : $"No matches for \"{query}\"";
            EmptyListText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyListText.Visibility = Visibility.Collapsed;
        }
    }

    private static Brush ChipBrushFor(FsNode node, IReadOnlyDictionary<FsNode, int> rankOf, int branchCount)
    {
        if (!rankOf.TryGetValue(node, out int rank)) return Palette.AggregatedBrush;
        return Palette.BrushForBranch(rank, Math.Max(branchCount, 1), 1);
    }

    private void UpdateSelectedNodeInfo(FsNode? node)
    {
        if (node is null || ReferenceEquals(node, _viewRoot))
        {
            SelectedInfoBorder.Visibility = Visibility.Collapsed;
            SelectedNodeMeta.Text = "";
            SelectedPathText.Text = "";
            return;
        }

        string kind = node.IsDirectory ? "Folder"
            : (node.Flags & NodeFlags.CloudPlaceholder) != 0 ? "Cloud placeholder"
            : (node.Flags & NodeFlags.ReparsePoint) != 0 ? "Link"
            : (node.Flags & NodeFlags.Compressed) != 0 ? "Compressed file"
            : (node.Flags & NodeFlags.SparseFile) != 0 ? "Sparse file" : "File";

        double pct = node.Parent is { TotalAllocated: > 0 } p ? (double)node.TotalAllocated / p.TotalAllocated : 0;
        SelectedNodeMeta.Text = $"{SizeFormatter.Format(node.TotalAllocated)} · {pct:P1} · {kind}";
        SelectedPathText.Text = node.GetPath();
        SelectedInfoBorder.Visibility = Visibility.Visible;
    }

    private void SyncListHover(FsNode? node)
    {
        if (node is null || ChildrenList.ItemsSource is not IEnumerable<FileItemView> items) return;
        foreach (FileItemView item in items)
        {
            if (ReferenceEquals(item.Node, node))
            {
                ChildrenList.ScrollIntoView(item);
                return;
            }
        }
    }

    private void SyncListSelection(FsNode node)
    {
        if (ChildrenList.ItemsSource is not IEnumerable<FileItemView> items) return;
        foreach (FileItemView item in items)
        {
            if (ReferenceEquals(item.Node, node))
            {
                if (!ReferenceEquals(ChildrenList.SelectedItem, item))
                    ChildrenList.SelectedItem = item;
                ChildrenList.ScrollIntoView(item);
                return;
            }
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (IsInitialized && !_suppressFilterRebuild) RebuildFileList();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
    }

    private void ClearSearchFilter()
    {
        if (SearchBox.Text.Length == 0) return;
        _suppressFilterRebuild = true;
        SearchBox.Clear();
        _suppressFilterRebuild = false;
    }

    private void OnChildSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ChildrenList.SelectedItem is not FileItemView item) return;
        if (!item.IsAggregated)
        {
            _selectedNode = item.Node;
            UpdateSelectedNodeInfo(item.Node);
            Burst.SelectedSource = item.Node;
        }
    }

    private void OnChildDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedChild();

    private void OnChildrenKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenSelectedChild(); e.Handled = true; }
        else if (e.Key == Key.Back) { NavigateUp(); e.Handled = true; }
    }

    private void OpenSelectedChild()
    {
        if (ChildrenList.SelectedItem is not FileItemView item) return;
        if (item.IsAggregated) return;
        if (item.Node.IsDirectory) NavigateInto(item.Node);
        else RevealInExplorer(item.Node);
    }

    private void OnListBoxItemPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private FsNode? _sunburstContextNode;

    private void OnBurstContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        Point p = Mouse.GetPosition(Burst);
        var (node, isCenter) = Burst.HitTestTarget(p);
        if (node is null)
        {
            e.Handled = true;
            return;
        }

        _sunburstContextNode = node;
        if (!isCenter)
        {
            _selectedNode = node;
            UpdateSelectedNodeInfo(node);
            Burst.SelectedSource = node;
            SyncListSelection(node);
        }
    }

    private void OnRevealSunburstContextClick(object sender, RoutedEventArgs e)
    {
        if (_sunburstContextNode is { } node)
            RevealInExplorer(node);
    }

    private void OnCopyPathSunburstContextClick(object sender, RoutedEventArgs e)
    {
        if (_sunburstContextNode is { } node)
        {
            string path = node.GetPath();
            if (path.Length > 0) Clipboard.SetText(path);
        }
    }

    private void OnRevealContextClick(object sender, RoutedEventArgs e)
    {
        if (ChildrenList.SelectedItem is FileItemView { IsAggregated: false } item)
            RevealInExplorer(item.Node);
    }

    private void OnCopyPathContextClick(object sender, RoutedEventArgs e)
    {
        if (ChildrenList.SelectedItem is FileItemView { IsAggregated: false } item)
        {
            string path = item.Node.GetPath();
            if (path.Length > 0) Clipboard.SetText(path);
        }
    }

    private void OnRevealSelectedClick(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is { } node) RevealInExplorer(node);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_scanner.IsScanning) { _scanner.Cancel(); e.Handled = true; return; }
            if (SearchBox.Text.Length > 0) { SearchBox.Clear(); e.Handled = true; return; }
        }

        if (e.Key == Key.Back && Keyboard.FocusedElement is not TextBox)
        {
            OnBackClick(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            OnBackClick(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            OnForwardClick(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && !_scanner.IsScanning && _viewRoot is not null)
        {
            OnRescanClick(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnRescanClick(object sender, RoutedEventArgs e)
    {
        if (!_scanner.IsScanning && _result is not null)
        {
            StartScan(_result.Volume?.RootPath ?? _viewRoot?.GetPath() ?? "C:\\", _result.EngineName ?? "auto");
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder,
        uint cidl,
        IntPtr apidl,
        uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void ILFree(IntPtr pidl);

    private static void RevealInExplorer(FsNode node)
    {
        string path = node.GetPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        // Ensure drive roots retain trailing backslash (e.g. C:\ not C:)
        if (path.Length == 2 && path[1] == ':') path += "\\";

        // If the path does not exist directly on disk (e.g. NTFS system metadata like $MFT,
        // or virtual nodes like [orphaned]), find the nearest existing parent directory.
        string? targetPath = path;
        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            FsNode? parent = node.Parent;
            targetPath = null;
            while (parent is not null)
            {
                string p = parent.GetPath();
                if (p.Length == 2 && p[1] == ':') p += "\\";
                if (Directory.Exists(p))
                {
                    targetPath = p;
                    break;
                }
                parent = parent.Parent;
            }
        }

        if (string.IsNullOrEmpty(targetPath)) return;

        try
        {
            // Primary: use official Shell32 API (avoids CLI quote-parsing bugs in explorer.exe)
            IntPtr pidl = ILCreateFromPathW(targetPath);
            if (pidl != IntPtr.Zero)
            {
                try
                {
                    if (SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0) == 0)
                        return;
                }
                finally
                {
                    ILFree(pidl);
                }
            }

            // Fallback: If it's a file or specific folder, select it; if it's a drive root, open it directly
            if (File.Exists(targetPath) || (Directory.Exists(targetPath) && !targetPath.EndsWith(":\\") && !targetPath.EndsWith(":/")))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{targetPath.TrimEnd('\\')}\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true,
                });
            }
        }
        catch { }
    }

    private void RequestElevatedRestart(string path)
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)) return;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Verb = "runas",
                UseShellExecute = true,
            };
            start.ArgumentList.Add("--scan");
            start.ArgumentList.Add(path);
            start.ArgumentList.Add("--fast");
            Process.Start(start);
            Application.Current.Shutdown();
        }
        catch { }
    }

    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool PathsEqual(string? left, string? right) => left is not null && right is not null &&
        string.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

