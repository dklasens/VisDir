using System.Windows.Media;
using SkiaSharp;

namespace VisDir.App.Sunburst;

public static class Palette
{
    private static readonly float[] CuratedHues =
    [
        145f, // Mint green
        82f,  // Chartreuse lime
        32f,  // Warm peach / amber
        258f, // Lavender / purple
        188f, // Cyan / sky blue
        215f, // Soft blue
        340f, // Coral / rose
        48f,  // Daisy gold
        285f, // Orchid magenta
        165f, // Emerald teal
    ];

    /// <summary>Vivid harmonic hue for a top-level branch (DaisyDisk rainbow order).</summary>
    public static float HueForBranch(int index, int count)
    {
        if (index < 0) return 225f;
        if (count <= CuratedHues.Length && index < CuratedHues.Length)
        {
            return CuratedHues[index];
        }
        return (145f + index * (360f / Math.Max(count, 1))) % 360f;
    }

    public static SKColor ColorFor(SunburstNode node, bool hovered)
    {
        if (node.IsAggregatedWedge || node.BranchIndex < 0 && node.Depth > 0)
        {
            float light = hovered ? 0.48f : 0.38f;
            return SKColor.FromHsl(225f, 12f, light * 100f);
        }
        if (node.Depth == 0)
        {
            return CenterFill;
        }

        return ColorForBranch(node.BranchIndex, node.BranchCount, node.Depth, hovered);
    }

    public static SKColor ColorForBranch(int branchIndex, int branchCount, int depth = 1, bool hovered = false)
    {
        if (branchIndex < 0)
        {
            float aggLight = hovered ? 0.48f : 0.38f;
            return SKColor.FromHsl(225f, 12f, aggLight * 100f);
        }

        float hue = HueForBranch(branchIndex, branchCount);
        float sat = 0.65f;
        float light = depth switch
        {
            1 => 0.62f,
            2 => 0.56f,
            3 => 0.60f,
            4 => 0.54f,
            5 => 0.58f,
            _ => 0.53f,
        };

        if (hovered) light = MathF.Min(0.92f, light + 0.14f);

        return SKColor.FromHsl(hue, sat * 100f, light * 100f);
    }

    public static Brush BrushForBranch(int branchIndex, int branchCount, int depth = 1)
    {
        SKColor sk = ColorForBranch(branchIndex, branchCount, depth, hovered: false);
        return CreateFrozenBrush(sk.Red, sk.Green, sk.Blue);
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
    public static readonly SKColor LabelHalo = new(0x1A, 0x1D, 0x2B, 0xE0);
}
