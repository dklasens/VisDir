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
    private readonly ScanService _scanner;
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
    private readonly DirectoryViewSession _viewSession = new();
    private int _listSequence;
    private Task _listBuildTask = Task.CompletedTask;
    private CancellationTokenSource? _listCancellation;
    private int _pageIndex;
    private bool _showAllItems;
    private bool _refreshingDrives;
    private bool _closed;

    public MainWindow() : this(new ScanService()) { }

    internal MainWindow(ScanService scanner)
    {
        _scanner = scanner;
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

        // ScanService coalesces progress. Preserve its phase transitions and endpoints
        // when forwarding callbacks to the dispatcher.
        _scanner.ProgressChanged += OnScanProgressChanged;
        _scanner.StatusChanged += status =>
        {
            int scanId = _scanner.ScanId;
            Dispatcher.BeginInvoke(() =>
            {
                if (!_closed && _scanner.IsCurrent(scanId)) ScanPhaseText.Text = status;
            });
        };
        _scanner.Completed += r =>
        {
            int scanId = _scanner.ScanId;
            Dispatcher.BeginInvoke(() => { _ = OnScanCompletedAsync(r, scanId); });
        };
        _scanner.Failed += msg => Dispatcher.BeginInvoke(() =>
        {
            if (_closed || _scanner.IsScanning) return;
            ScanOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, msg, "Scan Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (_result is null) ShowLandingView();
        });
        _scanner.Cancelled += () => Dispatcher.BeginInvoke(() =>
        {
            if (_closed || _scanner.IsScanning) return;
            ScanOverlay.Visibility = Visibility.Collapsed;
            if (_result is null) ShowLandingView();
        });

        Burst.HoveredChanged += _ => Dispatcher.Invoke(() =>
        {
            // The footer and its Explorer action always describe the persistent selection.
            UpdateSelectedNodeInfo(_selectedNode);
        });
        Burst.NodeSelected += node =>
        {
            _selectedNode = node;
            UpdateSelectedNodeInfo(node);
            SyncListSelection(node);
        };
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
        Burst.AggregateClicked += ShowAllItems;

        Closed += (_, _) =>
        {
            _closed = true;
            _listCancellation?.Cancel();
            _listCancellation?.Dispose();
            _viewSession.Dispose();
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

    private async void RefreshDrives(bool onlyWhenChanged = false)
    {
        if (_refreshingDrives || _closed) return;
        _refreshingDrives = true;
        try
        {
            List<DriveChoice> fresh = await Task.Run(() =>
            {
                var choices = new List<DriveChoice>();
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
                        ulong total = (ulong)drive.TotalSize;
                        ulong free = Math.Min(total, (ulong)drive.AvailableFreeSpace);
                        string rootLabel = drive.Name.TrimEnd('\\');
                        choices.Add(new DriveChoice
                        {
                            DisplayName = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? rootLabel : $"{drive.VolumeLabel} ({rootLabel})",
                            RootPath = drive.RootDirectory.FullName,
                            CapacityText = $"{SizeFormatter.Format(total)} capacity",
                            FreeText = $"{SizeFormatter.Format(free)} free of {SizeFormatter.Format(total)}",
                            UsedFraction = total > 0 ? (double)(total - free) / total : 0,
                            IsVolumeRoot = true,
                            SupportsFastScan = string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
                            TotalBytes = total, FreeBytes = free,
                        });
                    }
                    catch { /* Removed or temporarily unavailable media. */ }
                }
                return choices.OrderBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase).ToList();
            });
            if (_closed) return;
            fresh.AddRange(_customTargets);
            string signature = string.Join('|', fresh.Select(d => $"{d.RootPath}:{d.DisplayName}:{d.FreeBytes}"));
            string previous = string.Join('|', _drives.Select(d => $"{d.RootPath}:{d.DisplayName}:{d.FreeBytes}"));
            if (onlyWhenChanged && signature == previous) return;
            string? selectedPath = (DriveCards.SelectedItem as DriveChoice)?.RootPath;
            _drives = fresh;
            DriveCards.ItemsSource = _drives;
            DriveCards.SelectedItem = fresh.FirstOrDefault(d => PathsEqual(d.RootPath, selectedPath)) ?? fresh.FirstOrDefault();
        }
        catch { /* Retain the last successful drive inventory. */ }
        finally { _refreshingDrives = false; }
    }

    private void ClearViewState()
    {
        _viewSession.Reset(null);
        _listCancellation?.Cancel();
        _listCancellation?.Dispose();
        _listCancellation = null;
        _listBuildTask = Task.CompletedTask;
        Interlocked.Increment(ref _layoutSequence);
        Interlocked.Increment(ref _listSequence);
        _filterDebounce.Stop();
        _result = null;
        _viewRoot = null;
        _selectedNode = null;
        _sunburstContextNode = null;
        _backHistory.Clear();
        _forwardHistory.Clear();
        Burst.PendingLayout = null;
        Burst.ViewRoot = null;
        Burst.Volume = null;
        Burst.SelectedSource = null;
        _fileItems.Clear();
        _fileItemByNode.Clear();
        ListResultCount.Text = "";
        ListPagingButtons.Visibility = Visibility.Collapsed;
        ShowAllItemsButton.Visibility = Visibility.Collapsed;
        EmptyListText.Visibility = Visibility.Collapsed;
        _pageIndex = 0;
        _showAllItems = false;
        ClearSearchFilter();
        UpdateSelectedNodeInfo(null);
    }

    private void ShowLandingView()
    {
        if (_scanner.IsScanning) _scanner.Cancel();
        ClearViewState();
        ScanOverlay.Visibility = Visibility.Collapsed;
        LandingPanel.Visibility = Visibility.Visible;
        ContentShell.Visibility = Visibility.Collapsed;
        EngineBadge.Visibility = Visibility.Collapsed;
        ScanWarningBadge.Visibility = Visibility.Collapsed;
        FastScanButton.Visibility = Visibility.Collapsed;
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

        LandingPanel.Visibility = Visibility.Collapsed;
        ContentShell.Visibility = Visibility.Visible;
        ScanOverlay.Visibility = Visibility.Visible;
        ScanWarningBadge.Visibility = Visibility.Collapsed;
        ScanOverlayTarget.Text = path;
        ScanProgressBar.IsIndeterminate = true;
        ScanPhaseText.Text = "Initializing scan…";
        DisksRootButton.IsEnabled = true;

        ClearViewState();
        BreadcrumbBar.Children.Clear();
        EngineBadge.Visibility = Visibility.Collapsed;
        FastScanButton.Visibility = Visibility.Collapsed;
        RescanButton.Visibility = Visibility.Collapsed;
        UpdateHistoryButtons();
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

    private void OnScanProgressChanged(double fraction)
    {
        int scanId = _scanner.ScanId;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || !_scanner.IsCurrent(scanId)) return;
            // Known fractions describe either MFT records or snapshot nodes loaded. The
            // unknown-length directory walk reports -1 and keeps its marquee.
            if (double.IsFinite(fraction) && fraction is >= 0 and <= 1)
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

    private async Task OnScanCompletedAsync(ScanResult result, int scanId)
    {
        if (!_scanner.IsCurrent(scanId) || _closed) return;
        _result = result;
        _viewSession.Reset(result.Root);
        long generation = _viewSession.Generation;
        CancellationToken token = _viewSession.Token;
        try
        {
            ScanProgressBar.IsIndeterminate = true;
            ScanPhaseText.Text = "Preparing disk map…";
            SunburstNode layout = await Task.Run(() => SunburstLayout.Build(result.Root,
                maxDepth: SunburstControl.MaxVisibleDepth, cancellationToken: token), token);
            if (!_scanner.IsCurrent(scanId) || !_viewSession.IsCurrent(generation, result.Root) || _closed) return;
            string engineKey = result.EngineName.Trim().ToLowerInvariant();
            EngineBadgeText.Text = engineKey == "mft" ? "FAST NTFS" : "COMPATIBLE";
            EngineBadge.ToolTip = engineKey == "mft"
                ? "Fast NTFS reads filesystem metadata, including allocated streams and directory indexes."
                : "Compatible scan uses Windows directory enumeration. Some protected files may be unavailable.";
            EngineBadge.Visibility = Visibility.Visible;
            RescanButton.Visibility = Visibility.Visible;
            FastScanButton.Visibility = engineKey != "mft" && result.RequestedEngine != "mft" && !IsElevated()
                ? Visibility.Visible : Visibility.Collapsed;
            ScanWarningText.Text = result.Stats.ErrorCount > 0 ? $"{result.Stats.ErrorCount:N0} scan issues"
                : result.Completeness == ScanCompleteness.Partial ? "Partial scan" : "Scan details";
            ScanWarningBadge.Visibility = result.Completeness == ScanCompleteness.Partial || result.Issues.Count > 0 ||
                result.Stats.ErrorCount > 0 || result.FallbackReason is not null
                ? Visibility.Visible : Visibility.Collapsed;
            ScanWarningBadge.ToolTip = "View scan limitations and affected locations";
            Burst.Volume = result.Volume;
            Burst.PendingLayout = layout;
            NavigateInto(result.Root, recordHistory: false);
            await _listBuildTask;
            if (!_scanner.IsCurrent(scanId) || !_viewSession.IsCurrent(generation, result.Root) || _closed) return;
            ScanOverlay.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed && _scanner.IsCurrent(scanId))
            {
                ScanOverlay.Visibility = Visibility.Collapsed;
                MessageBox.Show(this, ex.Message, "Could not prepare disk map", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { _scanner.AcceptResult(scanId); }
    }

    private void OnScanWarningBadgeClick(object sender, RoutedEventArgs e)
    {
        if (_result is not { } result) return;
        string details = string.Join("\n", result.Issues.Take(12).Select(issue => $"{issue.Path}: {issue.Reason}"));
        string summary = result.Stats.ErrorCount > 0
            ? $"{result.Stats.ErrorCount:N0} location(s) could not be fully measured. Reported sizes may be incomplete."
            : "Scan details";
        if (result.FallbackReason is { Length: > 0 } fallback) summary += $"\n\n{fallback}";
        if (details.Length > 0) summary += $"\n\n{details}";
        if (result.Issues.Count > 12) summary += $"\n…and {result.Issues.Count - 12:N0} more reported locations.";
        MessageBox.Show(this, summary, "Scan details", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnFastScanClick(object sender, RoutedEventArgs e)
    {
        if (_viewRoot is { } root) StartScan(root.GetPath(), "mft");
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
        if (!ReferenceEquals(_viewSession.Root, node)) _viewSession.Reset(node);
        _viewRoot = node;
        _selectedNode = node;
        _pageIndex = 0;
        _showAllItems = false;

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
        CancellationToken token = _viewSession.Token;
        try
        {
            SunburstNode layout = await Task.Run(() => SunburstLayout.Build(node,
                maxDepth: SunburstControl.MaxVisibleDepth, cancellationToken: token), token);
            if (seq != Volatile.Read(ref _layoutSequence) || !ReferenceEquals(_viewRoot, node) || _closed) return;
            Burst.PendingLayout = layout;
            Burst.ViewRoot = node;
        }
        catch (OperationCanceledException) { }
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

    private void RebuildFileList() => _listBuildTask = RebuildFileListAsync();

    private async Task RebuildFileListAsync()
    {
        if (_viewRoot is not { } root) return;
        string query = SearchBox.Text.Trim();
        bool showAll = _showAllItems;
        int requestedPage = _pageIndex;
        int sequence = Interlocked.Increment(ref _listSequence);
        _listCancellation?.Cancel();
        _listCancellation?.Dispose();
        _listCancellation = CancellationTokenSource.CreateLinkedTokenSource(_viewSession.Token);
        CancellationToken token = _listCancellation.Token;
        try
        {
            DirectoryPage page = await Task.Run(() => DirectoryViewModel.Build(root, query, showAll, requestedPage, token), token);
            if (sequence != _listSequence || !ReferenceEquals(root, _viewRoot) || _closed) return;
            _pageIndex = page.PageIndex;
            int branchCount = Math.Max(1, page.BranchRanks.Count);
            Brush[] brushes = Enumerable.Range(0, branchCount).Select(i => Palette.BrushForBranch(i, branchCount)).ToArray();
            CurrentFolderDot.Background = brushes[0];
            Brush normal = (Brush)FindResource("TextBrush");
            Brush dim = (Brush)FindResource("DimBrush");
            FsNode? selection = _selectedNode;
            var byNode = new Dictionary<FsNode, FileItemView>(ReferenceEqualityComparer.Instance);
            var wanted = new List<FileItemView>(page.Nodes.Count + 1);
            foreach (FsNode node in page.Nodes)
            {
                double fraction = root.TotalAllocated > 0 ? (double)node.TotalAllocated / root.TotalAllocated : 0;
                string tip = $"{node.Name}\n{SizeFormatter.Format(node.TotalAllocated)} on disk · {(node.IsDirectory ? "Folder" : "File")} · {fraction:P1} of this folder";
                Brush chip = ChipBrushFor(node, page.BranchRanks, brushes);
                if (!_fileItemByNode.TryGetValue(node, out FileItemView? item))
                    item = new FileItemView(node, chip, normal, normal, tip);
                else item.Refresh(chip, normal, normal, tip);
                byNode[node] = item;
                wanted.Add(item);
            }
            if (page.IncludesSmallerGroup)
            {
                var node = new FsNode { Name = $"smaller objects… ({page.SmallerCount:N0})", TotalAllocated = page.SmallerBytes, Flags = NodeFlags.Directory };
                wanted.Add(new FileItemView(node, Palette.AggregatedBrush, dim, dim,
                    "Open to browse every item in this folder", isAggregated: true));
            }
            _fileItems.Clear();
            foreach (FileItemView row in wanted) _fileItems.Add(row);
            _fileItemByNode = byNode;
            if (selection is not null && byNode.TryGetValue(selection, out FileItemView? selected)) ChildrenList.SelectedItem = selected;
            int first = page.MatchCount == 0 ? 0 : page.PageIndex * DirectoryPage.PageSize + 1;
            int last = Math.Min(page.MatchCount, (page.PageIndex + 1) * DirectoryPage.PageSize);
            ListResultCount.Text = $"{first:N0}–{last:N0} of {page.MatchCount:N0} {(query.Length > 0 ? "matches" : "items")}";
            if (!showAll && query.Length == 0 && root.TotalAllocated > 0 && page.SmallerCount > 0)
                ListResultCount.Text += $" · {page.TotalCount:N0} total";
            PreviousPageButton.IsEnabled = page.PageIndex > 0;
            NextPageButton.IsEnabled = page.PageIndex + 1 < page.PageCount;
            ListPagingButtons.Visibility = page.PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
            ShowAllItemsButton.Visibility = query.Length == 0 && page.SmallerCount > 0 && root.TotalAllocated > 0
                ? Visibility.Visible : Visibility.Collapsed;
            ShowAllItemsButton.Content = showAll ? "Group smaller items" : "Show all items";
            EmptyListText.Text = query.Length == 0 ? "This folder is empty" : $"No matches for \"{query}\"";
            EmptyListText.Visibility = page.MatchCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
    }

    private void ShowAllItems(FsNode parent)
    {
        if (!ReferenceEquals(parent, _viewRoot)) NavigateInto(parent);
        _filterDebounce.Stop();
        ClearSearchFilter();
        _showAllItems = true;
        _pageIndex = 0;
        RebuildFileList();
    }

    private void OnShowAllItemsClick(object sender, RoutedEventArgs e)
    {
        _showAllItems = !_showAllItems;
        _pageIndex = 0;
        RebuildFileList();
    }

    private void OnPreviousPageClick(object sender, RoutedEventArgs e)
    {
        _pageIndex = Math.Max(0, _pageIndex - 1);
        RebuildFileList();
    }

    private void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        _pageIndex++;
        RebuildFileList();
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
        _pageIndex = 0;
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
        if (item.IsAggregated) { if (_viewRoot is { } root) ShowAllItems(root); return; }
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
            NavigateUp();
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
            StartScan(_viewRoot?.GetPath() ?? _result.Root.GetPath(), "auto");
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
        // A download is already in flight: starting another would share an
        // exclusively-locked path and fail with a sharing violation.
        if (_isDownloadingUpdate) return;

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
        var downloadCts = new CancellationTokenSource();
        _updateDownloadCts?.Dispose();
        _updateDownloadCts = downloadCts;
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
                _availableRelease, progress, downloadCts.Token);
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
            UpdateService.LogDownloadError(ex);
            MessageBox.Show(
                this,
                $"Failed to download update:\n{ex.Message}\n\n[{ex.GetType().Name} (0x{ex.HResult:X8}) — full trace in %TEMP%\\VisDir\\Updates\\download-error.log]",
                "Update Download Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            // Only clear what we created: a newer attempt owns its own CTS.
            if (ReferenceEquals(_updateDownloadCts, downloadCts))
            {
                downloadCts.Dispose();
                _updateDownloadCts = null;
            }
            else downloadCts.Dispose();
        }
    }
}
