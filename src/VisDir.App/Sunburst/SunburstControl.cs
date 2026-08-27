using System.Diagnostics;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using VisDir.Core;

namespace VisDir.App.Sunburst;

public class SunburstControl : SkiaSharp.Views.WPF.SKElement
{
    public const int MaxVisibleDepth = 6;

    // Center hole fraction of the chart radius; ring bands fill the rest of the radius.
    private const double InnerRadiusFraction = 0.20;
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(280);

    private SunburstNode? _layout;
    private SunburstNode? _hovered;
    private readonly List<SunburstNode> _visibleNodes = [];
    private readonly List<CachedArc> _cachedArcs = [];
    private readonly List<CachedCapacityArc> _capacityArcs = [];
    private readonly List<LegendItem> _legendItems = [];
    private int _cacheWidth;
    private int _cacheHeight;
    private int _keyboardIndex = -1;

    // Typeface objects own native handles. Keep one set for the process lifetime rather
    // than allocating undisposed handles on every paint/animation frame.
    private static readonly SKTypeface RegularTypeface = SKTypeface.FromFamilyName("Segoe UI");
    private static readonly SKTypeface MediumTypeface = SKTypeface.FromFamilyName(
        "Segoe UI", SKFontStyleWeight.Medium, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
    private static readonly SKTypeface BoldTypeface = SKTypeface.FromFamilyName(
        "Segoe UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

    // Drill transition state: new layout rendered under an interpolated
    // angle/depth transform that starts at the previous view's framing.
    private bool _animating;
    private bool _animBloom; // first paint after a scan: gentle scale + fade
    private double _animAngleFrom;
    private double _animScaleFrom = 1;
    private double _animDepthFrom;
    private readonly Stopwatch _animClock = new();

    private sealed record CachedArc(SunburstNode Node, SKPath Path, float StrokeWidth);
    private sealed record CachedCapacityArc(SKPath Path, SKColor Color, float StrokeWidth);
    private sealed record LegendItem(SKColor Color, string Text);

    public static readonly DependencyProperty ViewRootProperty = DependencyProperty.Register(
        nameof(ViewRoot), typeof(FsNode), typeof(SunburstControl),
        new FrameworkPropertyMetadata(null, OnViewRootChanged));

    public FsNode? ViewRoot
    {
        get => (FsNode?)GetValue(ViewRootProperty);
        set => SetValue(ViewRootProperty, value);
    }

    /// <summary>Volume totals for the free-space/metadata wedges; null hides them.</summary>
    public VolumeInfo? Volume { get; set; }

    private FsNode? _selectedSource;

    /// <summary>
    /// Persistently highlighted node (mirrors the contents-list selection). Unlike hover it survives
    /// the mouse leaving; ignored when it equals <see cref="ViewRoot"/> (the whole ring isn't worth ringing).
    /// </summary>
    public FsNode? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!ReferenceEquals(_selectedSource, value))
            {
                _selectedSource = value;
                InvalidateVisual();
            }
        }
    }

    public event Action<FsNode?>? HoveredChanged;
    public event Action<FsNode>? NodeClicked;
    public event Action? CenterClicked;

    private static void OnViewRootChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (SunburstControl)d;
        SunburstNode? oldLayout = c._layout;
        var oldVisible = c._visibleNodes.ToArray();

        c._hovered = null;
        c._selectedSource = null;
        c._keyboardIndex = -1;
        c._layout = e.NewValue is FsNode root ? SunburstLayout.Build(root) : null;
        AutomationProperties.SetName(c, e.NewValue is FsNode namedRoot
            ? $"Disk usage sunburst for {namedRoot.Name}, {SizeFormatter.Format(namedRoot.TotalAllocated)}"
            : "Disk usage sunburst");
        c.RebuildVisibleNodes();
        c.ClearRenderCache();
        c.BeginTransition(oldLayout, oldVisible, e.NewValue as FsNode);
        c.HoveredChanged?.Invoke(null);
        c.InvalidateVisual();
    }

    private void BeginTransition(SunburstNode? oldLayout, SunburstNode[] oldVisible, FsNode? newRoot)
    {
        _animating = false;
        _animBloom = false;
        CompositionTarget.Rendering -= OnAnimationTick;
        if (newRoot is null || ActualWidth < 2) return;

        if (oldLayout is null)
        {
            _animBloom = true;
        }
        else if (FindBySource(oldVisible, newRoot) is { Sweep: > 0.02 } into)
        {
            // Drill-in: the clicked wedge expands to the full circle.
            _animAngleFrom = into.Angle0;
            _animScaleFrom = into.Sweep / SunburstLayout.FullCircle;
            _animDepthFrom = into.Depth;
        }
        else if (FindBySource(_visibleNodes, oldLayout.Source) is { } upFrom &&
                 upFrom.Sweep is > 0.001 && upFrom.Sweep < SunburstLayout.FullCircle - 0.001)
        {
            // Drill-up: the old root shrinks back into its wedge inside the parent view.
            _animAngleFrom = -upFrom.Angle0 * SunburstLayout.FullCircle / upFrom.Sweep;
            _animScaleFrom = SunburstLayout.FullCircle / upFrom.Sweep;
            _animDepthFrom = -1;
        }
        else
        {
            _animBloom = true;
        }

        _animating = true;
        _animClock.Restart();
        CompositionTarget.Rendering += OnAnimationTick;
    }

    private static SunburstNode? FindBySource(IEnumerable<SunburstNode> nodes, FsNode source)
    {
        foreach (SunburstNode n in nodes)
            if (ReferenceEquals(n.Source, source)) return n;
        return null;
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_animClock.Elapsed >= AnimationDuration)
        {
            _animating = false;
            _animBloom = false;
            CompositionTarget.Rendering -= OnAnimationTick;
        }
        InvalidateVisual();
    }

    private static double AnimationProgress(bool animating, Stopwatch clock)
    {
        double t = animating ? Math.Min(1.0, clock.Elapsed.TotalMilliseconds / AnimationDuration.TotalMilliseconds) : 1.0;
        // cubic ease in-out
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ClearRenderCache();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        SetHovered(null);
        if (_hoverCenter)
        {
            _hoverCenter = false;
            InvalidateVisual();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_animating) return;
        Point p = e.GetPosition(this);
        SunburstNode? hit = HitTestAt(p);
        SetHovered(hit);

        bool isOverCenter = hit is null && RadiusFractionFromPoint(p) < InnerRadiusFraction;
        if (_hoverCenter != isOverCenter)
        {
            _hoverCenter = isOverCenter;
            InvalidateVisual();
        }

        Cursor = hit is { Depth: > 0, IsAggregatedWedge: false } ? Cursors.Hand
            : isOverCenter && ViewRoot?.Parent is not null ? Cursors.Hand
            : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Focus();
        if (_animating) return;
        var p = e.GetPosition(this);

        double r = RadiusFractionFromPoint(p);
        if (r < InnerRadiusFraction)
        {
            CenterClicked?.Invoke();
            return;
        }

        var hit = HitTestAt(p);
        if (hit is not null && !hit.IsAggregatedWedge && hit.Depth > 0)
            NodeClicked?.Invoke(hit.Source);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        List<SunburstNode> choices = _visibleNodes
            .Where(n => n.Depth > 0 && !n.IsAggregatedWedge)
            .ToList();
        if (choices.Count == 0) return;

        switch (e.Key)
        {
            case Key.Left:
            case Key.Up:
                _keyboardIndex = (_keyboardIndex - 1 + choices.Count) % choices.Count;
                SelectKeyboardNode(choices[_keyboardIndex]);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Down:
                _keyboardIndex = (_keyboardIndex + 1) % choices.Count;
                SelectKeyboardNode(choices[_keyboardIndex]);
                e.Handled = true;
                break;
            case Key.Home:
                _keyboardIndex = 0;
                SelectKeyboardNode(choices[0]);
                e.Handled = true;
                break;
            case Key.End:
                _keyboardIndex = choices.Count - 1;
                SelectKeyboardNode(choices[^1]);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                if (_keyboardIndex < 0) _keyboardIndex = 0;
                NodeClicked?.Invoke(choices[Math.Min(_keyboardIndex, choices.Count - 1)].Source);
                e.Handled = true;
                break;
            case Key.Back:
                CenterClicked?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private void SelectKeyboardNode(SunburstNode node)
    {
        _selectedSource = node.Source;
        SetHovered(node);
        AutomationProperties.SetName(this,
            $"Disk usage sunburst. Selected {node.DisplayName}, {SizeFormatter.Format(node.Source.TotalAllocated)}");
        InvalidateVisual();
    }

    private void SetHovered(SunburstNode? node)
    {
        FsNode? source = node is { Depth: > 0 } ? node.Source : null;
        bool changed = !ReferenceEquals(_hovered, node);
        _hovered = node;
        if (changed)
        {
            HoveredChanged?.Invoke(source);
            InvalidateVisual();
        }
    }

    /// <summary>Chart geometry in pixels for a surface of the given size.</summary>
    private static (float cx, float cy, float radius, float inner, float band, float ringW) GeometryFor(float w, float h)
    {
        float maxR = MathF.Min(w, h) / 2f * 0.88f;
        float inner = maxR * (float)InnerRadiusFraction;
        float band = (maxR - inner) / (MaxVisibleDepth + 1);
        float ringW = band - MathF.Max(2.5f, maxR * 0.010f);
        return (w / 2f, h / 2f, maxR, inner, band, ringW);
    }

    private (double radius, double inner, double band, double ringW) GeometryDip()
    {
        double w = Math.Max(ActualWidth, 1), h = Math.Max(ActualHeight, 1);
        double maxR = Math.Min(w, h) / 2 * 0.88;
        double inner = maxR * InnerRadiusFraction;
        double band = (maxR - inner) / (MaxVisibleDepth + 1);
        double ringW = band - Math.Max(2.5, maxR * 0.010);
        return (maxR, inner, band, ringW);
    }

    private double RadiusFractionFromPoint(Point p)
    {
        var (radius, _, _, _) = GeometryDip();
        double cx = Math.Max(ActualWidth, 1) / 2, cy = Math.Max(ActualHeight, 1) / 2;
        return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)) / radius;
    }

    public (FsNode? Node, bool IsCenter) HitTestTarget(Point p)
    {
        if (_layout is null || ViewRoot is null) return (null, false);
        double r = RadiusFractionFromPoint(p);
        if (r < InnerRadiusFraction)
        {
            return (ViewRoot, true);
        }
        var hit = HitTestAt(p);
        if (hit is not null && !hit.IsAggregatedWedge && hit.Depth > 0)
        {
            return (hit.Source, false);
        }
        return (null, false);
    }

    private SunburstNode? HitTestAt(Point p)
    {
        if (_layout is null || ViewRoot is null) return null;
        var (radius, inner, band, ringW) = GeometryDip();
        if (radius <= 0) return null;
        double cx = Math.Max(ActualWidth, 1) / 2, cy = Math.Max(ActualHeight, 1) / 2;
        double dx = p.X - cx, dy = p.Y - cy;
        double rf = Math.Sqrt(dx * dx + dy * dy) / radius;
        double angle = Math.Atan2(dy, dx);
        if (angle < 0) angle += 2 * Math.PI;

        return SunburstLayout.HitTest(
            _layout, rf, angle,
            inner / radius, ringW / radius, (band - ringW) / radius, MaxVisibleDepth);
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        SKCanvas canvas = e.Surface.Canvas;
        int width = e.Info.Width, height = e.Info.Height;
        canvas.Clear(SystemParameters.HighContrast ? CanvasBackground : Palette.Background);

        if (_layout is null || ViewRoot is null) return;

        float scale = (float)(width / Math.Max(ActualWidth, 1)); // DPI scaling
        var g = GeometryFor(width, height);

        if (_animating)
        {
            DrawAnimated(canvas, g, AnimationProgress(true, _animClock));
        }
        else
        {
            EnsureRenderCache(width, height, g);
            DrawTree(canvas);
            DrawLabels(canvas, g, scale);
            DrawLegend(canvas, height, scale);
        }
        DrawCenter(canvas, g, scale);
        if (IsKeyboardFocused)
        {
            using var focusPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(2f, 2f * scale),
                Color = SystemParameters.HighContrast ? CanvasAccent : new SKColor(0xFF, 0xD4, 0x86),
                IsAntialias = true,
            };
            canvas.DrawRoundRect(new SKRect(2 * scale, 2 * scale, width - 2 * scale, height - 2 * scale),
                6 * scale, 6 * scale, focusPaint);
        }
    }

    private void RebuildVisibleNodes()
    {
        _visibleNodes.Clear();
        if (_layout is null) return;
        var stack = new Stack<SunburstNode>();
        stack.Push(_layout);
        while (stack.Count > 0)
        {
            SunburstNode node = stack.Pop();
            if (node.Depth > MaxVisibleDepth || node.Sweep <= 0) continue;
            _visibleNodes.Add(node);
            if (node.AggregatedWedge is { } aggregate) stack.Push(aggregate);
            if (node.Children is { } children)
                for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    private void EnsureRenderCache(int width, int height, (float cx, float cy, float radius, float inner, float band, float ringW) g)
    {
        if (_cachedArcs.Count > 0 && _cacheWidth == width && _cacheHeight == height) return;
        ClearRenderCache();
        _cacheWidth = width;
        _cacheHeight = height;

        foreach (SunburstNode node in _visibleNodes)
        {
            float rMid = g.inner + node.Depth * g.band + g.ringW / 2;
            var rect = new SKRect(g.cx - rMid, g.cy - rMid, g.cx + rMid, g.cy + rMid);
            var path = CreateArcPath(rect, Degrees(node.Angle0), Degrees(node.Sweep));
            _cachedArcs.Add(new CachedArc(node, path, g.ringW));
        }

        if (ViewRoot is { Parent: null } viewRoot && Volume is { TotalBytes: > 0 } volume)
        {
            ulong accounted = Math.Min(viewRoot.TotalAllocated, volume.TotalBytes);
            ulong used = volume.TotalBytes >= volume.FreeBytes ? volume.TotalBytes - volume.FreeBytes : accounted;
            ulong metadata = used > accounted ? used - accounted : 0;
            ulong free = volume.TotalBytes > used ? volume.TotalBytes - used : 0;
            float rMid = g.inner + g.ringW / 2;
            var rect = new SKRect(g.cx - rMid, g.cy - rMid, g.cx + rMid, g.cy + rMid);
            double cursor = 0;
            AddCapacityArc(accounted, Palette.ScannedVolume, "Scanned");
            AddCapacityArc(metadata, Palette.MetadataWedge, "System & other");
            AddCapacityArc(free, Palette.FreeSpace, "Free");

            void AddCapacityArc(ulong bytes, SKColor color, string label)
            {
                if (bytes == 0) return;
                double sweep = SunburstLayout.FullCircle * bytes / volume.TotalBytes;
                var path = CreateArcPath(rect, Degrees(cursor), Degrees(sweep));
                _capacityArcs.Add(new CachedCapacityArc(path, color, g.ringW));
                _legendItems.Add(new LegendItem(color, $"{label} · {SizeFormatter.Format(bytes)}"));
                cursor += sweep;
            }
        }
    }

    private void ClearRenderCache()
    {
        foreach (CachedArc arc in _cachedArcs) arc.Path.Dispose();
        foreach (CachedCapacityArc arc in _capacityArcs) arc.Path.Dispose();
        _cachedArcs.Clear();
        _capacityArcs.Clear();
        _legendItems.Clear();
        _cacheWidth = _cacheHeight = 0;
    }

    private void DrawTree(SKCanvas canvas)
    {
        // While a wedge is hovered, unrelated branches recede so the active lineage reads instantly.
        bool dimActive = _hovered is { Depth: > 0 };
        int focusBranch = dimActive && _hovered!.IsAggregatedWedge ? int.MinValue : _hovered?.BranchIndex ?? 0;

        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Butt };
        foreach (CachedCapacityArc arc in _capacityArcs)
        {
            stroke.StrokeWidth = arc.StrokeWidth;
            stroke.Color = arc.Color;
            canvas.DrawPath(arc.Path, stroke);
        }
        foreach (CachedArc arc in _cachedArcs)
        {
            if (_capacityArcs.Count > 0 && arc.Node.Depth == 0) continue;
            SKColor color = Palette.ColorFor(arc.Node, ReferenceEquals(arc.Node, _hovered));
            if (ShouldDim(arc.Node, dimActive, focusBranch)) color = color.WithAlpha(DimmedAlpha);
            stroke.StrokeWidth = arc.StrokeWidth;
            stroke.Color = color;
            canvas.DrawPath(arc.Path, stroke);
        }

        // Persistent selection ring (list selection / clicked file), beneath the brighter hover ring.
        if (_selectedSource is { } selected && !ReferenceEquals(selected, ViewRoot) &&
            FindCachedArc(selected) is { } selArc)
        {
            stroke.StrokeWidth = selArc.StrokeWidth + 2;
            stroke.Color = Palette.ColorFor(selArc.Node, true);
            canvas.DrawPath(selArc.Path, stroke);
        }
        if (FindCachedArc(_hovered is { Depth: > 0 } ? _hovered.Source : null) is { } hoveredArc)
        {
            stroke.StrokeWidth = hoveredArc.StrokeWidth + 3;
            stroke.Color = Palette.ColorFor(hoveredArc.Node, true);
            canvas.DrawPath(hoveredArc.Path, stroke);
        }
    }

    private const byte DimmedAlpha = 150;

    /// <summary>True when <paramref name="node"/> sits outside the hovered branch's lineage.</summary>
    private static bool ShouldDim(SunburstNode node, bool dimActive, int focusBranch) =>
        dimActive && node.Depth > 0 &&
        (node.IsAggregatedWedge ? focusBranch != int.MinValue : node.BranchIndex != focusBranch);

    private CachedArc? FindCachedArc(FsNode? source)
    {
        if (source is null) return null;
        foreach (CachedArc arc in _cachedArcs)
            if (ReferenceEquals(arc.Node.Source, source)) return arc;
        return null;
    }

    private void DrawAnimated(SKCanvas canvas, (float cx, float cy, float radius, float inner, float band, float ringW) g, double t)
    {
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Butt, StrokeWidth = g.ringW };
        float bloomScale = _animBloom ? (float)Lerp(0.88, 1.0, t) : 1f;
        byte bloomAlpha = _animBloom ? (byte)(255 * t) : (byte)255;
        double angleOffset = _animBloom ? 0 : Lerp(_animAngleFrom, 0, t);
        double angleScale = _animBloom ? 1 : Lerp(_animScaleFrom, 1, t);
        double depthOffset = _animBloom ? 0 : Lerp(_animDepthFrom, 0, t);

        foreach (SunburstNode node in _visibleNodes)
        {
            double sweep = node.Sweep * angleScale;
            if (sweep < 0.003) continue;
            double depth = node.Depth + depthOffset;
            if (depth > MaxVisibleDepth) continue;
            float rMid = (float)(g.inner + depth * g.band + g.ringW / 2) * bloomScale;
            if (rMid <= g.ringW / 2) continue; // fully inside the center hole

            var rect = new SKRect(g.cx - rMid, g.cy - rMid, g.cx + rMid, g.cy + rMid);
            using var path = CreateArcPath(rect, Degrees(angleOffset + node.Angle0 * angleScale), Degrees(sweep));
            SKColor color = Palette.ColorFor(node, false);
            stroke.Color = bloomAlpha == 255 ? color : color.WithAlpha(bloomAlpha);
            canvas.DrawPath(path, stroke);
        }
    }

    private static readonly SKColor LabelColor = new(0xFF, 0xFF, 0xFF, 0xEE);
    private static readonly SKColor LabelDimColor = new(0xFF, 0xFF, 0xFF, 0x70);

    private void DrawLabels(SKCanvas canvas, (float cx, float cy, float radius, float inner, float band, float ringW) g, float scale)
    {
        bool dimActive = _hovered is { Depth: > 0 };
        int focusBranch = dimActive && _hovered!.IsAggregatedWedge ? int.MinValue : _hovered?.BranchIndex ?? 0;

        using var labelPaint = new SKPaint
        {
            Color = LabelColor,
            IsAntialias = true,
        };
        using var labelFont = new SKFont(MediumTypeface, 10.5f * scale);
        using var haloPaint = new SKPaint
        {
            Color = Palette.LabelHalo,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f * scale,
            StrokeJoin = SKStrokeJoin.Round,
        };

        foreach (CachedArc arc in _cachedArcs)
        {
            SunburstNode node = arc.Node;
            if (node.Depth < 1) continue;
            float rMid = g.inner + node.Depth * g.band + g.ringW / 2;
            double arcLen = node.Sweep * rMid;
            if (arcLen < 52 * scale) continue;

            // Only label wedges where the full name fits comfortably — partial
            // labels and ellipses read as visual noise on a dense chart.
            string text = node.DisplayName.TrimEnd('\\');
            if (text.Length == 0 || labelFont.MeasureText(text, labelPaint) > arcLen - 10 * scale) continue;
            labelPaint.Color = SystemParameters.HighContrast
                ? CanvasForeground
                : ShouldDim(node, dimActive, focusBranch) ? LabelDimColor : LabelColor;
            haloPaint.Color = SystemParameters.HighContrast ? CanvasBackground : Palette.LabelHalo;

            canvas.Save();
            canvas.RotateRadians((float)node.MidAngle, g.cx, g.cy);
            canvas.Translate(g.cx + rMid, g.cy);
            canvas.RotateDegrees(90);
            double degrees = node.MidAngle * 180 / Math.PI;
            if (degrees is > 90 and < 270) canvas.RotateDegrees(180); // keep text upright on the left half
            canvas.DrawText(text, 0, labelFont.Size * 0.34f, SKTextAlign.Center, labelFont, haloPaint);
            canvas.DrawText(text, 0, labelFont.Size * 0.34f, SKTextAlign.Center, labelFont, labelPaint);
            canvas.Restore();
        }
    }

    private static string? TruncateToFit(SKFont font, SKPaint paint, string text, float maxW)
    {
        const string ellipsis = "…";
        int length = text.Length;
        while (length > 0 && font.MeasureText(text[..length] + ellipsis, paint) > maxW) length--;
        return length >= 5 ? text[..length] + ellipsis : null;
    }

    private void DrawLegend(SKCanvas canvas, float height, float scale)
    {
        if (_legendItems.Count == 0) return;
        using var textPaint = new SKPaint
        {
            Color = SystemParameters.HighContrast ? CanvasForeground : new SKColor(0xA9, 0xAD, 0xBA),
            IsAntialias = true,
        };
        using var textFont = new SKFont(RegularTypeface, 10.5f * scale);
        using var chipPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var chipBorder = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f * scale, Color = new SKColor(0x3A, 0x3E, 0x4A) };

        float x = 18 * scale;
        float y = height - 20 * scale;
        float chip = 9 * scale;
        foreach (LegendItem item in _legendItems)
        {
            chipPaint.Color = item.Color;
            canvas.DrawRoundRect(x, y - chip, chip, chip, 2.5f * scale, 2.5f * scale, chipPaint);
            canvas.DrawRoundRect(x, y - chip, chip, chip, 2.5f * scale, 2.5f * scale, chipBorder);
            x += chip + 5 * scale;
            canvas.DrawText(item.Text, x, y, SKTextAlign.Left, textFont, textPaint);
            x += textFont.MeasureText(item.Text, textPaint) + 18 * scale;
        }
    }

    private bool _hoverCenter;

    private static (string number, string unit) SplitSize(ulong bytes)
    {
        string formatted = SizeFormatter.Format(bytes);
        int lastSpace = formatted.LastIndexOf(' ');
        if (lastSpace > 0)
            return (formatted[..lastSpace], formatted[(lastSpace + 1)..]);
        return (formatted, "");
    }

    private void DrawCenter(SKCanvas canvas, (float cx, float cy, float radius, float inner, float band, float ringW) g, float scale)
    {
        FsNode root = ViewRoot!;
        bool isHoveringWedge = _hovered is { Depth: > 0 };
        FsNode shown = isHoveringWedge ? _hovered!.Source : (_selectedSource ?? root);
        bool isShowingRoot = ReferenceEquals(shown, root);

        // Center circle background with subtle ring border
        using (var fill = new SKPaint { Color = SystemParameters.HighContrast ? CanvasBackground : Palette.CenterFill, IsAntialias = true, Style = SKPaintStyle.Fill })
            canvas.DrawCircle(g.cx, g.cy, g.inner - 1, fill);

        using (var rim = new SKPaint { Color = SystemParameters.HighContrast ? CanvasForeground : new SKColor(0x2E, 0x34, 0x48), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * scale })
            canvas.DrawCircle(g.cx, g.cy, g.inner - 1, rim);

        float maxW = g.inner * 2f * 0.82f;
        var accentColor = SystemParameters.HighContrast ? CanvasAccent : new SKColor(0x5C, 0xD6, 0x8D);

        if (isShowingRoot)
        {
            var (number, unit) = SplitSize(root.TotalAllocated);
            using var numPaint = new SKPaint
            {
                Color = SystemParameters.HighContrast ? CanvasForeground : SKColors.White,
                IsAntialias = true,
            };
            using var numFont = new SKFont(BoldTypeface, 20 * scale);
            using var unitPaint = new SKPaint
            {
                Color = accentColor,
                IsAntialias = true,
            };
            using var unitFont = new SKFont(BoldTypeface, 14 * scale);

            while (numFont.Size > 12 * scale && numFont.MeasureText(number, numPaint) > maxW) numFont.Size -= 1f;

            canvas.DrawText(number, g.cx, g.cy - 1 * scale, SKTextAlign.Center, numFont, numPaint);
            canvas.DrawText(unit, g.cx, g.cy + 17 * scale, SKTextAlign.Center, unitFont, unitPaint);

            if (_hoverCenter && root.Parent is not null)
            {
                using var hintPaint = new SKPaint
                {
                    Color = SystemParameters.HighContrast ? CanvasForeground : new SKColor(0x8A, 0x92, 0xA8),
                    IsAntialias = true,
                };
                using var hintFont = new SKFont(RegularTypeface, 9 * scale);
                canvas.DrawText("click to go up", g.cx, g.cy + 30 * scale,
                    SKTextAlign.Center, hintFont, hintPaint);
            }
        }
        else
        {
            string name = shown.Name.TrimEnd('\\');
            if (name.Length == 0) name = shown.Name;
            string size = SizeFormatter.Format(shown.TotalAllocated);

            using var namePaint = new SKPaint
            {
                Color = SystemParameters.HighContrast ? CanvasForeground : SKColors.White,
                IsAntialias = true,
            };
            using var nameFont = new SKFont(MediumTypeface, 13.5f * scale);
            using var sizePaint = new SKPaint
            {
                Color = accentColor,
                IsAntialias = true,
            };
            using var sizeFont = new SKFont(BoldTypeface, 15 * scale);

            while (nameFont.Size > 9.5f * scale && nameFont.MeasureText(name, namePaint) > maxW) nameFont.Size -= 1f;
            if (nameFont.MeasureText(name, namePaint) > maxW)
                name = TruncateToFit(nameFont, namePaint, name, maxW) ?? "…";

            canvas.DrawText(name, g.cx, g.cy - 3 * scale, SKTextAlign.Center, nameFont, namePaint);
            canvas.DrawText(size, g.cx, g.cy + 15 * scale, SKTextAlign.Center, sizeFont, sizePaint);
        }
    }

    private static SKPath CreateArcPath(SKRect bounds, float startAngle, float sweepAngle)
    {
        using var builder = new SKPathBuilder();
        builder.AddArc(bounds, startAngle, sweepAngle);
        return builder.Detach();
    }

    private static SKColor CanvasBackground => ToSkColor(SystemColors.WindowColor);
    private static SKColor CanvasForeground => ToSkColor(SystemColors.WindowTextColor);
    private static SKColor CanvasAccent => ToSkColor(SystemColors.HighlightColor);

    private static SKColor ToSkColor(System.Windows.Media.Color color) =>
        new(color.R, color.G, color.B, color.A);

    private static float Degrees(double radians) => (float)(radians * 180 / Math.PI);
}
