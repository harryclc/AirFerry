using AirFerry.Windows.Scan;
using Xunit;

namespace AirFerry.Windows.Tests;

public sealed class RoiRectTests
{
    [Fact]
    public void Clamp_Disabled_ReturnsFullFrame()
    {
        RoiRect roi = RoiRect.Clamp(new RoiRect(0, 0, 0, 0), 1920, 1080);
        Assert.Equal(new RoiRect(0, 0, 1920, 1080), roi);
    }

    [Fact]
    public void Clamp_InBounds_ReturnsSameRect()
    {
        var roi = new RoiRect(100, 50, 800, 800);
        Assert.Equal(roi, RoiRect.Clamp(roi, 1920, 1080));
    }

    [Fact]
    public void Clamp_OutOfBounds_IntersectsWithFrame()
    {
        RoiRect roi = RoiRect.Clamp(new RoiRect(1900, 1000, 500, 500), 1920, 1080);
        Assert.Equal(new RoiRect(1900, 1000, 20, 80), roi);
    }

    [Fact]
    public void Clamp_NegativeOrigin_ClampedToZero()
    {
        RoiRect roi = RoiRect.Clamp(new RoiRect(-50, -20, 100, 100), 1920, 1080);
        Assert.Equal(new RoiRect(0, 0, 50, 80), roi);
    }

    [Fact]
    public void Clamp_InvalidFrameSize_ReturnsEmptyRect()
    {
        RoiRect roi = RoiRect.Clamp(new RoiRect(0, 0, 800, 800), 0, 0);
        Assert.Equal(new RoiRect(0, 0, 0, 0), roi);
    }
}
