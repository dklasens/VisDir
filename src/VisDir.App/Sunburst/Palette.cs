using System.Windows.Media;
using SkiaSharp;

namespace VisDir.App.Sunburst;

public static class Palette
{
    /// <summary>
    /// Master harmonic hues for top-level branches (DaisyDisk-style vivid rainbow order).
    /// Selected for high perceptual contrast and aesthetic vibrancy.
    /// </summary>
    private static readonly float[] MasterHues =
    [
        155f, // Emerald Teal
        215f, // Royal Blue
        36f,  // Warm Amber / Gold
        335f, // Rose Pink / Coral
        275f, // Orchid Purple
        85f,  // Chartreuse Lime
        188f, // Electric Cyan
        308f, // Vivid Fuchsia
        52f,  // Daisy Gold
        125f, // Spring Green
        15f,  // Crimson Tangerine
        245f, // Deep Iris
        170f, // Mint
        355f, // Ruby
        200f, // Sky Blue
        260f, // Lavender
    ];

    /// <summary>
    /// Computes the base hue for a top-level branch, ensuring maximum harmonic spacing
    /// across the color wheel for any branch count.
    /// </summary>
    public static float HueForBranch(int index, int count)
    {
        if (index < 0) return 225f;
        count = Math.Max(count, 1);
        if (index >= count) index %= count;

        return count switch
        {
            1 => 160f,
            2 => index == 0 ? 155f : 345f,
            3 => index switch { 0 => 155f, 1 => 38f, _ => 218f },
            4 => index switch { 0 => 155f, 1 => 38f, 2 => 218f, _ => 335f },
            5 => index switch { 0 => 155f, 1 => 215f, 2 => 36f, 3 => 335f, _ => 275f },
            6 => index switch { 0 => 155f, 1 => 215f, 2 => 36f, 3 => 335f, 4 => 275f, _ => 85f },
            7 => index switch { 0 => 155f, 1 => 215f, 2 => 36f, 3 => 335f, 4 => 275f, 5 => 85f, _ => 188f },
            8 => index switch { 0 => 155f, 1 => 215f, 2 => 36f, 3 => 335f, 4 => 275f, 5 => 85f, 6 => 188f, _ => 308f },
            _ when count <= MasterHues.Length && index < MasterHues.Length => MasterHues[index],
            _ => (155f + index * (360f / count)) % 360f,
        };
    }

    /// <summary>
    /// Computes a tree-aware gradient color for a sunburst node.
    /// Applies an angular gradient across the branch sector (differentiating subfolders),
    /// a radial depth gradient (transitioning lightness and saturation outward),
    /// and sibling micro-contrast to clearly delineate directory structures.
    /// </summary>
    public static SKColor ColorFor(SunburstNode node, bool hovered)
    {
        if (node.Depth == 0)
        {
            return CenterFill;
        }

        if (node.IsAggregatedWedge || (node.BranchIndex < 0 && node.Depth > 0))
        {
            float aggHue = node.BranchIndex >= 0 ? HueForBranch(node.BranchIndex, node.BranchCount) : 225f;
            float aggSat = node.BranchIndex >= 0 ? 0.20f : 0.12f;
            float aggLight = hovered ? 0.52f : 0.36f;
            return SKColor.FromHsl(aggHue, aggSat * 100f, aggLight * 100f);
        }

        float baseHue = HueForBranch(node.BranchIndex, node.BranchCount);

        // 1. Angular Hue Gradient across the branch sector
        double branchSpan = node.BranchEndAngle - node.BranchStartAngle;
        float tAngle = 0.5f;
        if (branchSpan > 0.001)
        {
            tAngle = (float)Math.Clamp((node.MidAngle - node.BranchStartAngle) / branchSpan, 0.0, 1.0);
        }

        // Hue spread: allow the branch to gracefully fan out across its sector.
        // Scaled to branch count so adjacent branches never clash.
        float maxHueSpread = Math.Min(28f, 150f / Math.Max(node.BranchCount, 1));
        float angleHueOffset = (tAngle - 0.5f) * 2f * maxHueSpread;

        // 2. Depth progression (Radial gradient)
        int depth = Math.Clamp(node.Depth, 1, 6);
        float depthHueDrift = (depth - 1) * 2.5f;

        float hue = (baseHue + angleHueOffset + depthHueDrift + 360f) % 360f;

        // Radial Lightness gradient:
        // Depth 1: 0.53, Depth 2: 0.57, Depth 3: 0.61, Depth 4: 0.64, Depth 5: 0.67, Depth 6: 0.69
        float baseLight = depth switch
        {
            1 => 0.53f,
            2 => 0.57f,
            3 => 0.61f,
            4 => 0.64f,
            5 => 0.67f,
            _ => 0.69f,
        };

        // Subtle alternating ring boundary so concentric rings stay razor-sharp
        float ringStripe = (depth % 2 == 1) ? 0.015f : -0.015f;
        float light = baseLight + ringStripe;

        // 3. Sibling micro-contrast (Delineating adjacent subfolders)
        if (node.SiblingCount > 1)
        {
            float siblingOffset = (node.SiblingIndex % 2 == 1) ? 0.022f : -0.022f;
            light += siblingOffset;
        }

        // 4. Radial Saturation:
        float sat = depth switch
        {
            1 => 0.76f,
            2 => 0.73f,
            3 => 0.69f,
            4 => 0.66f,
            5 => 0.63f,
            _ => 0.60f,
        };

        // 5. Hover lighting
        if (hovered)
        {
            light = MathF.Min(0.93f, light + 0.16f);
            sat = MathF.Min(1.0f, sat + 0.10f);
        }

        return SKColor.FromHsl(hue, sat * 100f, Math.Clamp(light, 0.15f, 0.95f) * 100f);
    }

    /// <summary>
    /// Color for a branch at a given depth, matching the depth-1 anchor used in UI list chips.
    /// </summary>
    public static SKColor ColorForBranch(int branchIndex, int branchCount, int depth = 1, bool hovered = false)
    {
        if (branchIndex < 0)
        {
            float aggLight = hovered ? 0.48f : 0.38f;
            return SKColor.FromHsl(225f, 12f, aggLight * 100f);
        }

        float baseHue = HueForBranch(branchIndex, branchCount);
        int clampedDepth = Math.Clamp(depth, 1, 6);
        float depthHueDrift = (clampedDepth - 1) * 2.5f;
        float hue = (baseHue + depthHueDrift + 360f) % 360f;

        float baseLight = clampedDepth switch
        {
            1 => 0.53f,
            2 => 0.57f,
            3 => 0.61f,
            4 => 0.64f,
            5 => 0.67f,
            _ => 0.69f,
        };
        float ringStripe = (clampedDepth % 2 == 1) ? 0.015f : -0.015f;
        float light = baseLight + ringStripe;

        float sat = clampedDepth switch
        {
            1 => 0.76f,
            2 => 0.73f,
            3 => 0.69f,
            4 => 0.66f,
            5 => 0.63f,
            _ => 0.60f,
        };

        if (hovered)
        {
            light = MathF.Min(0.93f, light + 0.16f);
            sat = MathF.Min(1.0f, sat + 0.10f);
        }

        return SKColor.FromHsl(hue, sat * 100f, Math.Clamp(light, 0.15f, 0.95f) * 100f);
    }

    private static readonly Dictionary<(int BranchIndex, int BranchCount, int Depth), Brush> _brushCache = new();
    private static readonly object _brushLock = new();

    /// <summary>
    /// Frozen shared brush per (branch, count, depth). Frozen brushes are
    /// free-threaded, so off-UI-thread layout rebuilds may call this safely.
    /// </summary>
    public static Brush BrushForBranch(int branchIndex, int branchCount, int depth = 1)
    {
        var key = (branchIndex, branchCount, depth);
        lock (_brushLock)
        {
            if (_brushCache.TryGetValue(key, out Brush? cached)) return cached;
            SKColor sk = ColorForBranch(branchIndex, branchCount, depth, hovered: false);
            Brush brush = CreateFrozenBrush(sk.Red, sk.Green, sk.Blue);
            _brushCache[key] = brush;
            return brush;
        }
    }

    public static Brush AggregatedBrush { get; } = CreateFrozenBrush(0x52, 0x58, 0x6A);

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // DaisyDisk signature dark slate navy canvas background
    public static readonly SKColor Background = new(0x22, 0x26, 0x38);
    public static readonly SKColor CenterFill = new(0x1A, 0x1D, 0x2B);
    public static readonly SKColor FreeSpace = new(0x14, 0x16, 0x22);
    public static readonly SKColor MetadataWedge = new(0x32, 0x37, 0x4B);
    public static readonly SKColor PlaceholderFile = new(0x52, 0x58, 0x6A);
    public static readonly SKColor ScannedVolume = SKColor.FromHsl(210f, 65f, 60f);
}
