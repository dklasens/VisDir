using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
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
using VisDir.App.Update;
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

public sealed class FileItemView : INotifyPropertyChanged
{
    public FsNode Node { get; }
    public string Name => Node.Name;
    public string Kind => Node.IsDirectory ? "Folder" : "File";

    private ulong _bytes;
    private string _sizeText;
    /// <summary>Formatted size, computed once per node and refreshed only when the byte count changes.</summary>
    public string SizeText { get => _sizeText; private set => SetField(ref _sizeText, value); }
    private Brush _chipBrush;
    public Brush ChipBrush { get => _chipBrush; private set => SetField(ref _chipBrush, value); }
    private Brush _textBrush;
    public Brush TextBrush { get => _textBrush; private set => SetField(ref _textBrush, value); }
    private Brush _sizeBrush;
    public Brush SizeBrush { get => _sizeBrush; private set => SetField(ref _sizeBrush, value); }
    private string _toolTipText;
    public string ToolTipText { get => _toolTipText; private set => SetField(ref _toolTipText, value); }
    public bool IsAggregated { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileItemView(FsNode node, Brush chip, Brush textBrush, Brush sizeBrush, string toolTipText, bool isAggregated = false)
    {
        Node = node;
        _bytes = node.TotalAllocated;
        _sizeText = SizeFormatter.Format(_bytes);
        _chipBrush = chip;
        _textBrush = textBrush;
        _sizeBrush = sizeBrush;
        _toolTipText = toolTipText;
        IsAggregated = isAggregated;
    }

    /// <summary>Refresh cached display values after a diff-reuse; raises change only for altered properties.</summary>
    public void Refresh(Brush chip, Brush textBrush, Brush sizeBrush, string toolTipText)
    {
        ChipBrush = chip;
        TextBrush = textBrush;
        SizeBrush = sizeBrush;
        ToolTipText = toolTipText;
        if (Node.TotalAllocated != _bytes)
        {
            _bytes = Node.TotalAllocated;
            SizeText = SizeFormatter.Format(_bytes);
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

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
    private readonly DispatcherTimer _filterDebounce;
    private Dictionary<FsNode, FileItemView> _fileItemByNode = new(ReferenceEqualityComparer.Instance);
    /// <summary>Bound to <see cref="ChildrenList"/> once; rebuilds diff into it so virtualization
    /// containers (Recycling) survive filter keystrokes instead of regenerating from scratch.</summary>
    private readonly ObservableCollection<FileItemView> _fileItems = new();
    private FileItemView? _aggregatedItem;
    // Wedge-rank + chip-brush cache, valid for one folder view (identical across filter keystrokes).
    private FsNode? _rankRoot;
    private int _rankChildCount = -1;
    private ulong _rankTotal;
    private Dictionary<FsNode, int> _rankOf = new(ReferenceEqualityComparer.Instance);
    private Brush[] _branchBrushes = [];
    private long _lastProgressUiMs; // 10Hz gate for scan progress (Environment.TickCount64)

    public MainWindow()
    {
        InitializeComponent();
        ChildrenList.ItemsSource = _fileItems;
        ApplySystemContrast();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        SourceInitialized += (_, _) => EnableDarkTitleBar();
        RefreshDrives();

        _driveRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _driveRefreshTimer.Tick += (_, _) =>
        {
            if (!_scanner.IsScanning && LandingPanel.Visibility == Visibility.Visible)
                RefreshDrives(onlyWhenChanged: true);
        };
        _driveRefreshTimer.Start();

        _filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            if (!_suppressFilterRebuild) RebuildFileList();
        };

        // Scanner callbacks arrive on worker threads: never block them (BeginInvoke),
        // and coalesce progress to 10Hz (ScanService also gates at the source).
        _scanner.ProgressChanged += OnScanProgressThrottled;
        _scanner.StatusChanged += s => Dispatcher.BeginInvoke(() => ScanPhaseText.Text = s);
        _scanner.Completed += r => Dispatcher.BeginInvoke(() => { _ = OnScanCompletedAsync(r); });
        _scanner.Failed += msg => Dispatcher.BeginInvoke(() =>
        {
            ScanOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, msg, "Scan Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (_result is null) ShowLandingView();
        });
        _scanner.Cancelled += () => Dispatcher.BeginInvoke(() =>
        {
            ScanOverlay.Visibility = Visibility.Collapsed;
            if (_result is null) ShowLandingView();
        });

        Burst.HoveredChanged += node => Dispatcher.Invoke(() =>
        {
            // Hover drives the center readout only; the list no longer follows it.
            FsNode? target = node ?? _selectedNode ?? _viewRoot;
            UpdateSelectedNodeInfo(target);
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
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            _driveRefreshTimer.Stop();
            _filterDebounce.Stop();
            _updateDownloadCts?.Cancel();
            _updateDownloadCts?.Dispose();
            _scanner.Dispose();
        };

        Loaded += OnLoaded;
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.WindowGlassColor))
        {
            ApplySystemContrast();
            EnableDarkTitleBar();
            Burst.InvalidateVisual();
            if (_viewRoot is not null) RebuildFileList();
        }
    }

    private void ApplySystemContrast()
    {
        bool highContrast = SystemParameters.HighContrast;
        SetResourceBrush("WindowBgBrush", highContrast ? SystemColors.WindowColor : Color.FromRgb(0x22, 0x26, 0x38));
        SetResourceBrush("PanelBrush", highContrast ? SystemColors.WindowColor : Color.FromRgb(0x1D, 0x20, 0x2E));
        SetResourceBrush("CardBrush", highContrast ? SystemColors.WindowColor : Color.FromRgb(0x28, 0x2D, 0x40));
        SetResourceBrush("BorderBrush", highContrast ? SystemColors.WindowTextColor : Color.FromRgb(0x34, 0x3B, 0x52));
        SetResourceBrush("TextBrush", highContrast ? SystemColors.WindowTextColor : Color.FromRgb(0xF4, 0xF5, 0xF9));
        SetResourceBrush("DimBrush", highContrast ? SystemColors.WindowTextColor : Color.FromRgb(0x8E, 0x95, 0xAA));
        SetResourceBrush("AccentGreenBrush", highContrast ? SystemColors.HighlightColor : Color.FromRgb(0x5C, 0xD6, 0x8D));
        SetResourceBrush("AccentBlueBrush", highContrast ? SystemColors.HighlightColor : Color.FromRgb(0x4E, 0x75, 0xDB));
        SetResourceBrush("DeleteRedBrush", highContrast ? SystemColors.HighlightColor : Color.FromRgb(0xB3, 0x39, 0x42));
        SetResourceBrush("DockBgBrush", highContrast ? SystemColors.WindowColor : Color.FromRgb(0x18, 0x1B, 0x27));
        SetResourceBrush("SurfaceBrush", highContrast ? SystemColors.WindowColor : Color.FromRgb(0x2C, 0x33, 0x47));
        SetResourceBrush("InputBrush", highContrast ? SystemColors.WindowColor : Color.FromRgb(0x25, 0x2A, 0x3D));
        SetResourceBrush("ProgressTrackBrush", highContrast ? SystemColors.WindowColor : Color.FromRgb(0x38, 0x40, 0x58));
        SetResourceBrush("OverlayBrush", highContrast ? SystemColors.WindowColor : Color.FromArgb(0xEA, 0x22, 0x26, 0x38));
        SetResourceBrush("AccentTextBrush", highContrast ? SystemColors.HighlightTextColor : Colors.White);
        Background = (Brush)FindResource("WindowBgBrush");
        Foreground = (Brush)FindResource("TextBrush");
    }

    private void SetResourceBrush(string key, Color color)
    {
        if (Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = color;
        else
            Resources[key] = new SolidColorBrush(color);
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

        // The transactional updater waits for this signal before deleting its rollback copy.
        UpdateService.ReportHealthyStart();

        _ = CheckForUpdatesAsync(silent: true);

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
            if (SystemParameters.HighContrast)
            {
                int disabled = 0;
                DwmSetWindowAttribute(handle, 20, ref disabled, sizeof(int));
                int defaultColor = -1;
                DwmSetWindowAttribute(handle, 35, ref defaultColor, sizeof(int));
                DwmSetWindowAttribute(handle, 36, ref defaultColor, sizeof(int));
                return;
            }
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
        _result = null;
        _viewRoot = null;
        _selectedNode = null;
        _backHistory.Clear();
        _forwardHistory.Clear();
        Burst.ViewRoot = null;
        Burst.Volume = null;
        _fileItems.Clear();
        _aggregatedItem = null;
        _fileItemByNode.Clear();
        LandingPanel.Visibility = Visibility.Visible;
        ContentShell.Visibility = Visibility.Collapsed;
        EngineBadge.Visibility = Visibility.Collapsed;
        ScanWarningBadge.Visibility = Visibility.Collapsed;
        RescanButton.Visibility = Visibility.Collapsed;
        BreadcrumbBar.Children.Clear();
        DisksRootButton.IsEnabled = false;
        UpdateHistoryButtons();
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
        ScanWarningBadge.Visibility = Visibility.Collapsed;
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

        _fileItems.Clear();
        _aggregatedItem = null;
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

    /// <summary>10Hz-coalesced progress: drops bursts instead of queueing UI work, never blocks the worker.</summary>
    private void OnScanProgressThrottled(double fraction)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastProgressUiMs) < 100) return;
        Interlocked.Exchange(ref _lastProgressUiMs, now);
        Dispatcher.BeginInvoke(() =>
        {
            // Volume-based fractions sit near zero for most of a folder scan (bytes seen vs whole-disk
            // used space), which read as a frozen bar. Only go determinate on a real sweep (MFT read);
            // otherwise the marquee plus live file/folder counts carry the progress signal.
            if (fraction is >= 0.02 and < 1)
            {
                ScanProgressBar.IsIndeterminate = false;
                ScanProgressBar.Value = fraction;
            }
            else
            {
                ScanProgressBar.IsIndeterminate = true;
            }
        });
    }

    private async Task OnScanCompletedAsync(ScanResult result)
    {
        _result = result;

        EngineBadge.Visibility = Visibility.Visible;
        EngineBadgeText.Text = result.EngineName?.Trim().ToLowerInvariant() switch
        {
            "mft" => "FAST NTFS",
            "generic" or "compatible" => "COMPATIBLE",
            { Length: > 0 } other => other.ToUpperInvariant(),
            _ => "SCANNED",
        };
        RescanButton.Visibility = Visibility.Visible;
        if (result.Stats.ErrorCount > 0)
        {
            ScanWarningText.Text = $"{result.Stats.ErrorCount:N0} unreadable location{(result.Stats.ErrorCount == 1 ? "" : "s")}";
            ScanWarningBadge.ToolTip = "The displayed total is incomplete because one or more locations could not be read.";
            ScanWarningBadge.Visibility = Visibility.Visible;
        }
        else
        {
            ScanWarningBadge.Visibility = Visibility.Collapsed;
        }

        _backHistory.Clear();
        _forwardHistory.Clear();
        UpdateHistoryButtons();

        // First layout builds off-UI-thread (FsNode/SunburstNode are free-threaded);
        // the overlay stays indeterminate until NavigateInto returns. Bumping the
        // sequence abandons any in-flight navigation layout from the previous scan.
        ScanProgressBar.IsIndeterminate = true;
        Interlocked.Increment(ref _layoutSequence);
        Burst.PendingLayout = await Task.Run(() => SunburstLayout.Build(result.Root, maxDepth: SunburstControl.MaxVisibleDepth));
        Burst.Volume = result.Volume;
        NavigateInto(result.Root, recordHistory: false);
        ScanOverlay.Visibility = Visibility.Collapsed;
    }

    private int _layoutSequence;
    private async void NavigateInto(FsNode node, bool recordHistory = true)
    {
        if (!node.IsDirectory) return;

        if (recordHistory && _viewRoot is not null && !ReferenceEquals(_viewRoot, node) && !_navigatingHistory)
        {
            _backHistory.Push(_viewRoot);
            _forwardHistory.Clear();
            UpdateHistoryButtons();
        }

        // Layout rebuilds lazily per navigation, depth-capped by SunburstLayout: only the
        // entered subtree is built, and only down to MaxVisibleDepth.
        _filterDebounce.Stop();
        ClearSearchFilter();
        _viewRoot = node;
        _selectedNode = node;

        CurrentFolderName.Text = node.Name.TrimEnd('\\');
        if (CurrentFolderName.Text.Length == 0) CurrentFolderName.Text = node.Name;
        CurrentFolderTotalSize.Text = SizeFormatter.Format(node.TotalAllocated);

        RebuildBreadcrumbs();
        RebuildFileList();
        UpdateSelectedNodeInfo(node);
        UpdateHistoryButtons();

        // Heavy layout builds off-UI-thread; stale navigations are abandoned by sequence.
        // Reuses the PendingLayout fast path when the scan completion already prebuilt it.
        if (Burst.PendingLayout is { } pre && ReferenceEquals(pre.Source, node))
        {
            Burst.ViewRoot = node;
            return;
        }
        int seq = Interlocked.Increment(ref _layoutSequence);
        SunburstNode layout = await Task.Run(() => SunburstLayout.Build(node, maxDepth: SunburstControl.MaxVisibleDepth));
        if (seq != Volatile.Read(ref _layoutSequence)) return;
        if (!ReferenceEquals(_viewRoot, node)) return;
        Burst.PendingLayout = layout;
        Burst.ViewRoot = node;
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
        // Cap search materialization so a huge match set can't build unbounded rows.
        const int MaxSearchItems = 1000;
        if (query.Length > 0 && nodes.Count > MaxSearchItems) nodes.RemoveRange(MaxSearchItems, nodes.Count - MaxSearchItems);
        ulong viewTotal = _viewRoot.TotalAllocated;

        // Wedge ranks + chip brushes are identical for every keystroke within one folder view;
        // recompute only when the viewed folder, its child count, or its total changes.
        if (!ReferenceEquals(_rankRoot, _viewRoot) || _rankChildCount != allChildren.Count || _rankTotal != viewTotal)
        {
            var rankOf = new Dictionary<FsNode, int>(allChildren.Count, ReferenceEqualityComparer.Instance);
            int branchCount = 0;
            const double minSweep = 0.006;
            for (int i = 0; i < allChildren.Count; i++)
            {
                FsNode child = allChildren[i];
                bool isVisibleWedge = viewTotal == 0 || i == 0 ||
                    SunburstLayout.FullCircle * child.TotalAllocated / viewTotal >= minSweep;
                if (isVisibleWedge) rankOf[child] = branchCount++;
            }

            // One frozen brush per visible branch; rows share instances instead of allocating per row.
            int brushCount = Math.Max(branchCount, 1);
            var branchBrushes = new Brush[brushCount];
            for (int i = 0; i < brushCount; i++) branchBrushes[i] = Palette.BrushForBranch(i, brushCount, 1);

            _rankRoot = _viewRoot;
            _rankChildCount = allChildren.Count;
            _rankTotal = viewTotal;
            _rankOf = rankOf;
            _branchBrushes = branchBrushes;
        }

        // Set active folder dot color
        CurrentFolderDot.Background = _branchBrushes[0];

        var normalTextBrush = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        var dimTextBrush = TryFindResource("DimBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x8E, 0x95, 0xAA));

        FsNode? previouslySelected = (ChildrenList.SelectedItem as FileItemView)?.Node;

        // Build the desired order, reusing live row instances (with their cached SizeText/ToolTipText)
        // so surviving rows keep their virtualized containers and selection.
        var wanted = new List<FileItemView>(nodes.Count + 1);
        ulong aggregatedBytes = 0;
        int aggregatedCount = 0;
        foreach (FsNode node in nodes)
        {
            bool isVisible = _rankOf.ContainsKey(node);
            if (!isVisible && query.Length == 0)
            {
                aggregatedBytes += node.TotalAllocated;
                aggregatedCount++;
                continue;
            }

            double fraction = viewTotal > 0 ? (double)node.TotalAllocated / viewTotal : 0;
            string tip = $"{node.Name}\n{SizeFormatter.Format(node.TotalAllocated)} on disk · {(node.IsDirectory ? "Folder" : "File")} · {fraction:P1} of this folder";
            Brush chip = ChipBrushFor(node, _rankOf, _branchBrushes);
            if (!_fileItemByNode.TryGetValue(node, out FileItemView? item))
            {
                item = new FileItemView(node, chip, normalTextBrush, normalTextBrush, tip);
                _fileItemByNode[node] = item;
            }
            else
            {
                item.Refresh(chip, normalTextBrush, normalTextBrush, tip);
            }
            wanted.Add(item);
        }

        // DaisyDisk aggregated row: "smaller objects..." — one synthetic node reused across rebuilds.
        if (aggregatedCount > 0 && query.Length == 0)
        {
            string aggTip = $"{aggregatedCount} smaller items totaling {SizeFormatter.Format(aggregatedBytes)}";
            FileItemView agg;
            if (_aggregatedItem is { } kept)
            {
                kept.Node.TotalAllocated = aggregatedBytes;
                kept.Refresh(Palette.AggregatedBrush, dimTextBrush, dimTextBrush, aggTip);
                agg = kept;
            }
            else
            {
                var aggNode = new FsNode
                {
                    Name = "smaller objects...",
                    TotalAllocated = aggregatedBytes,
                    Flags = NodeFlags.Directory,
                };
                agg = new FileItemView(aggNode, Palette.AggregatedBrush, dimTextBrush, dimTextBrush, aggTip, isAggregated: true);
                _aggregatedItem = agg;
            }
            wanted.Add(agg);
        }
        else
        {
            _aggregatedItem = null;
        }

        // Bulk reconcile without O(n^2) IndexOf+Move: fast-path when already ordered,
        // otherwise clear+add. The 150ms debounce coalesces typing.
        bool inOrder = _fileItems.Count == wanted.Count;
        if (inOrder)
        {
            for (int i = 0; i < wanted.Count; i++)
                if (!ReferenceEquals(_fileItems[i], wanted[i])) { inOrder = false; break; }
        }
        if (!inOrder)
        {
            _fileItems.Clear();
            foreach (FileItemView item in wanted) _fileItems.Add(item);
        }

        // Drop lookup entries for nodes that left the view so the map cannot pin detached rows.
        var byNode = new Dictionary<FsNode, FileItemView>(wanted.Count, ReferenceEqualityComparer.Instance);
        foreach (FileItemView item in wanted)
            if (!item.IsAggregated) byNode[item.Node] = item;
        _fileItemByNode = byNode;

        if (previouslySelected is not null && byNode.TryGetValue(previouslySelected, out FileItemView? restore))
            ChildrenList.SelectedItem = restore;
        if (_fileItems.Count == 0)
        {
            EmptyListText.Text = query.Length == 0 ? "This folder is empty" : $"No matches for \"{query}\"";
            EmptyListText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyListText.Visibility = Visibility.Collapsed;
        }
    }

    private static Brush ChipBrushFor(FsNode node, IReadOnlyDictionary<FsNode, int> rankOf, Brush[] branchBrushes)
    {
        if (!rankOf.TryGetValue(node, out int rank) || (uint)rank >= (uint)branchBrushes.Length)
            return Palette.AggregatedBrush;
        return branchBrushes[rank];
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

    private void SyncListSelection(FsNode node)
    {
        if (_fileItemByNode.TryGetValue(node, out FileItemView? item))
        {
            if (!ReferenceEquals(ChildrenList.SelectedItem, item))
                ChildrenList.SelectedItem = item;
            ChildrenList.ScrollIntoView(item);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (!IsInitialized || _suppressFilterRebuild) return;
        _filterDebounce.Stop();
        _filterDebounce.Start();
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
            if (UpdateOverlay.Visibility == Visibility.Visible)
            {
                OnCloseUpdateOverlayClick(sender, e);
                e.Handled = true;
                return;
            }
            if (_scanner.IsScanning) { _scanner.Cancel(); e.Handled = true; return; }
            if (SearchBox.Text.Length > 0) { SearchBox.Clear(); e.Handled = true; return; }
        }

        if (e.Key == Key.Back && Keyboard.FocusedElement is not TextBox)
        {
            // Burst handles Back itself (go up); a window-level Back here would navigate twice.
            if (Burst.IsKeyboardFocused || ReferenceEquals(Keyboard.FocusedElement, Burst)) return;
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

    // Auto-Updater State & Handlers
    private readonly UpdateService _updateService = new();
    private ReleaseInfo? _availableRelease;
    private string? _downloadedZipPath;
    private bool _isDownloadingUpdate;
    private CancellationTokenSource? _updateDownloadCts;

    private async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var release = await _updateService.CheckForUpdatesAsync().ConfigureAwait(true);
            if (release is not null)
            {
                _availableRelease = release;
                UpdateBadgeText.Text = $"Update {release.TagName} available";
                UpdateBadgeButton.Visibility = Visibility.Visible;

                if (!silent)
                {
                    ShowUpdateOverlay(release);
                }
            }
            else if (!silent)
            {
                MessageBox.Show(
                    this,
                    $"You are using the latest version of VisDir (v{UpdateService.GetCurrentVersion()}).",
                    "VisDir Up to Date",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                MessageBox.Show(
                    this,
                    $"Could not check for updates:\n{ex.Message}",
                    "Update Check Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private void ShowUpdateOverlay(ReleaseInfo release)
    {
        UpdateTitleText.Text = $"Update Available: {release.TagName}";
        UpdateVersionSubtitle.Text = $"Current: v{UpdateService.GetCurrentVersion()}  ·  New: {release.TagName}";
        UpdateNotesText.Text = FormatReleaseNotes(release.ReleaseNotes);
        UpdateProgressPanel.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Value = 0;
        UpdateActionButton.Content = "Download & Install";
        UpdateActionButton.IsEnabled = true;
        UpdateCancelButton.IsEnabled = true;
        _downloadedZipPath = null;
        _isDownloadingUpdate = false;
        TopNavigationBar.IsEnabled = false;
        LandingPanel.IsEnabled = false;
        ContentShell.IsEnabled = false;
        UpdateOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => UpdateActionButton.Focus(), DispatcherPriority.Input);
    }

    private void OnUpdateBadgeClick(object sender, RoutedEventArgs e)
    {
        if (_availableRelease is not null)
        {
            ShowUpdateOverlay(_availableRelease);
        }
        else
        {
            _ = CheckForUpdatesAsync(silent: false);
        }
    }

    private void OnManualCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        _ = CheckForUpdatesAsync(silent: false);
    }

    private void OnCloseUpdateOverlayClick(object sender, RoutedEventArgs e)
    {
        if (_isDownloadingUpdate)
        {
            var result = MessageBox.Show(
                this,
                "A download is currently in progress. Do you want to cancel the update?",
                "Cancel Update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
            UpdateProgressText.Text = "Cancelling download…";
            _updateDownloadCts?.Cancel();
            return;
        }
        HideUpdateOverlay();
    }

    private void HideUpdateOverlay()
    {
        UpdateOverlay.Visibility = Visibility.Collapsed;
        TopNavigationBar.IsEnabled = true;
        LandingPanel.IsEnabled = true;
        ContentShell.IsEnabled = true;
    }

    private static string FormatReleaseNotes(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "No release notes provided.";
        return string.Join('\n', markdown.Replace("\r", "").Split('\n').Select(line =>
        {
            string text = line.TrimEnd();
            while (text.StartsWith('#')) text = text[1..].TrimStart();
            return text.Replace("**", "").Replace("`", "");
        })).Trim();
    }

    private void OnViewUpdateGitHubClick(object sender, RoutedEventArgs e)
    {
        string url = _availableRelease?.HtmlUrl ?? "https://github.com/dklasens/VisDir/releases";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private async void OnUpdateActionClick(object sender, RoutedEventArgs e)
    {
        if (_availableRelease is null) return;

        // If already downloaded, apply and restart
        if (!string.IsNullOrEmpty(_downloadedZipPath) && File.Exists(_downloadedZipPath))
        {
            try
            {
                UpdateService.ApplyUpdateAndRestart(_downloadedZipPath, _availableRelease.Sha256);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Failed to apply update:\n{ex.Message}",
                    "Update Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            return;
        }

        // Otherwise, download update
        _isDownloadingUpdate = true;
        _updateDownloadCts?.Dispose();
        _updateDownloadCts = new CancellationTokenSource();
        UpdateActionButton.IsEnabled = false;
        UpdateCancelButton.IsEnabled = true;
        UpdateCancelButton.Content = "Cancel";
        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        UpdateProgressText.Text = "Starting download…";

        var progress = new Progress<(long BytesDownloaded, long TotalBytes, double Fraction)>(p =>
        {
            UpdateProgressBar.Value = p.Fraction;
            string dl = SizeFormatter.Format((ulong)p.BytesDownloaded);
            string tot = SizeFormatter.Format((ulong)p.TotalBytes);
            UpdateProgressText.Text = $"Downloading: {dl} / {tot} ({p.Fraction:P0})";
        });

        try
        {
            string zip = await _updateService.DownloadUpdateAsync(
                _availableRelease, progress, _updateDownloadCts.Token);
            _downloadedZipPath = zip;
            _isDownloadingUpdate = false;

            UpdateProgressText.Text = "Download complete! Ready to install.";
            UpdateActionButton.Content = "Restart to Apply";
            UpdateActionButton.IsEnabled = true;
            UpdateCancelButton.IsEnabled = true;
            UpdateCancelButton.Content = "Later";
        }
        catch (OperationCanceledException)
        {
            _isDownloadingUpdate = false;
            UpdateActionButton.IsEnabled = true;
            UpdateCancelButton.IsEnabled = true;
            UpdateCancelButton.Content = "Later";
            UpdateProgressText.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            _isDownloadingUpdate = false;
            UpdateActionButton.IsEnabled = true;
            UpdateCancelButton.IsEnabled = true;
            UpdateCancelButton.Content = "Later";
            UpdateProgressText.Text = "Download failed.";
            MessageBox.Show(
                this,
                $"Failed to download update:\n{ex.Message}",
                "Update Download Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _updateDownloadCts?.Dispose();
            _updateDownloadCts = null;
        }
    }
}
