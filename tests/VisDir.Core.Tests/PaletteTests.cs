using SkiaSharp;
using VisDir.App.Sunburst;
using VisDir.Core;
using Xunit;

namespace VisDir.Core.Tests;

public class PaletteTests
{
    [Fact]
    public void HueForBranch_LowCounts_ReturnsMaximallySpacedDistinctHues()
    {
        // For count=5 (typical for C:\), hues should be thoroughly distributed across color spectrum
        int count = 5;
        var hues = new float[count];
        for (int i = 0; i < count; i++)
        {
            hues[i] = Palette.HueForBranch(i, count);
        }

        // Verify all 5 hues are well-separated (no two hues within 25 degrees of each other)
        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                float diff = MathF.Abs(hues[i] - hues[j]);
                if (diff > 180f) diff = 360f - diff;
                Assert.True(diff >= 25f, $"Hues {hues[i]} and {hues[j]} are too close (diff={diff})");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public void HueForBranch_VariousCounts_ProducesValidHues(int count)
    {
        for (int i = 0; i < count; i++)
        {
            float hue = Palette.HueForBranch(i, count);
            Assert.InRange(hue, 0f, 360f);
        }
    }

    [Fact]
    public void HueForBranch_NegativeIndex_ReturnsFallback()
    {
        float hue = Palette.HueForBranch(-1, 5);
        Assert.InRange(hue, 0f, 360f);
    }

    [Fact]
    public void ColorFor_GeneratesSectorAngularGradient_ForSubfolders()
    {
        // Create a root and branch that spans 0 to PI
        var dummy = new FsNode { Name = "test" };
        var leftSubfolder = new SunburstNode
        {
            Source = dummy,
            Depth = 2,
            Angle0 = 0.1,
            Angle1 = 0.3,
            BranchIndex = 0,
            BranchCount = 3,
            BranchStartAngle = 0.0,
            BranchEndAngle = Math.PI,
            SiblingIndex = 0,
            SiblingCount = 2,
        };

        var rightSubfolder = new SunburstNode
        {
            Source = dummy,
            Depth = 2,
            Angle0 = 2.8,
            Angle1 = 3.0,
            BranchIndex = 0,
            BranchCount = 3,
            BranchStartAngle = 0.0,
            BranchEndAngle = Math.PI,
            SiblingIndex = 1,
            SiblingCount = 2,
        };

        SKColor leftColor = Palette.ColorFor(leftSubfolder, hovered: false);
        SKColor rightColor = Palette.ColorFor(rightSubfolder, hovered: false);

        // Subfolders on opposite sides of the sector must have distinct gradient colors
        Assert.NotEqual(leftColor, rightColor);

        // Extract HSL hues
        leftColor.ToHsl(out float hLeft, out _, out _);
        rightColor.ToHsl(out float hRight, out _, out _);
        float hueDiff = MathF.Abs(hLeft - hRight);
        if (hueDiff > 180f) hueDiff = 360f - hueDiff;
        Assert.True(hueDiff > 10f, $"Expected angular gradient hue difference > 10 deg, but got {hueDiff}");
    }

    [Fact]
    public void ColorFor_GeneratesRadialDepthGradient()
    {
        var dummy = new FsNode { Name = "test" };
        SKColor[] depthColors = new SKColor[6];
        for (int d = 1; d <= 6; d++)
        {
            var node = new SunburstNode
            {
                Source = dummy,
                Depth = d,
                Angle0 = 0.5,
                Angle1 = 1.0,
                BranchIndex = 1,
                BranchCount = 4,
                BranchStartAngle = 0.0,
                BranchEndAngle = 1.5,
                SiblingIndex = 0,
                SiblingCount = 1,
            };
            depthColors[d - 1] = Palette.ColorFor(node, hovered: false);
        }

        // Each depth ring should be distinguishable from its neighbors
        for (int i = 0; i < depthColors.Length - 1; i++)
        {
            Assert.NotEqual(depthColors[i], depthColors[i + 1]);
        }

        // Lightness should generally increase outward
        depthColors[0].ToHsl(out _, out _, out float l1);
        depthColors[5].ToHsl(out _, out _, out float l6);
        Assert.True(l6 > l1, $"Expected outer ring (l={l6}) to be lighter than inner ring (l={l1})");
    }

    [Fact]
    public void ColorFor_HoverState_IsBrighterThanNormal()
    {
        var node = new SunburstNode
        {
            Source = new FsNode { Name = "test" },
            Depth = 1,
            Angle0 = 0.0,
            Angle1 = 1.0,
            BranchIndex = 0,
            BranchCount = 3,
            BranchStartAngle = 0.0,
            BranchEndAngle = 1.0,
            SiblingIndex = 0,
            SiblingCount = 1,
        };

        SKColor normal = Palette.ColorFor(node, hovered: false);
        SKColor hovered = Palette.ColorFor(node, hovered: true);

        normal.ToHsl(out _, out _, out float normalL);
        hovered.ToHsl(out _, out _, out float hoveredL);
        Assert.True(hoveredL > normalL, $"Hovered lightness {hoveredL} should exceed normal {normalL}");
    }

    [Fact]
    public void ColorFor_AggregatedWedge_ReturnsValidColor()
    {
        var agg = new SunburstNode
        {
            Source = new FsNode { Name = "smaller objects..." },
            Depth = 2,
            Angle0 = 1.0,
            Angle1 = 1.2,
            BranchIndex = 0,
            BranchCount = 4,
            BranchStartAngle = 0.0,
            BranchEndAngle = 1.2,
            IsAggregatedWedge = true,
        };

        SKColor c = Palette.ColorFor(agg, hovered: false);
        Assert.True(c.Alpha > 0);
    }

    [Fact]
    public void BrushForBranch_CachesAndFreezesBrush()
    {
        var brush1 = Palette.BrushForBranch(0, 5, 1);
        var brush2 = Palette.BrushForBranch(0, 5, 1);

        Assert.NotNull(brush1);
        Assert.True(brush1.IsFrozen);
        Assert.Same(brush1, brush2);
    }
}
