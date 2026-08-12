using AirFerry.Windows.Scan;
using Xunit;

namespace AirFerry.Windows.Tests;

public sealed class ScreenSelectorTests
{
    private static readonly IReadOnlyList<ScreenInfo> Screens =
    [
        new ScreenInfo(0, "\\.\\DISPLAY1", 1920, 1080, 0, 0, true, 0),
        new ScreenInfo(1, "\\.\\DISPLAY2", 2560, 1440, 1920, 0, false, 0),
    ];

    [Fact]
    public void Resolve_Primary_ReturnsPrimaryScreen()
    {
        ScreenInfo? screen = ScreenSelector.Resolve("primary", Screens);
        Assert.NotNull(screen);
        Assert.True(screen!.IsPrimary);
        Assert.Equal("\\.\\DISPLAY1", screen.DeviceName);
    }

    [Fact]
    public void Resolve_DeviceName_MatchesStableKey()
    {
        ScreenInfo? screen = ScreenSelector.Resolve("\\.\\DISPLAY2", Screens);
        Assert.NotNull(screen);
        Assert.Equal(1, screen!.Index);
        Assert.Equal(2560, screen.Width);
    }

    [Fact]
    public void Resolve_Index_ReturnsScreenByEnumerationOrder()
    {
        ScreenInfo? screen = ScreenSelector.Resolve("1", Screens);
        Assert.NotNull(screen);
        Assert.Equal(1, screen!.Index);
    }

    [Fact]
    public void Resolve_OutOfRangeIndex_ReturnsNull()
    {
        Assert.Null(ScreenSelector.Resolve("9", Screens));
    }

    [Fact]
    public void Resolve_UnknownDeviceName_ReturnsNull()
    {
        Assert.Null(ScreenSelector.Resolve("\\.\\DISPLAY99", Screens));
    }

    [Fact]
    public void Resolve_EmptyList_ReturnsNull()
    {
        Assert.Null(ScreenSelector.Resolve("primary", Array.Empty<ScreenInfo>()));
    }

    [Fact]
    public void Resolve_PrimaryWithoutFlag_FallsBackToFirstScreen()
    {
        var list = new[]
        {
            new ScreenInfo(0, "\\.\\DISPLAY1", 1920, 1080, 0, 0, false, 0),
            new ScreenInfo(1, "\\.\\DISPLAY2", 1920, 1080, 1920, 0, false, 0),
        };
        ScreenInfo? screen = ScreenSelector.Resolve("primary", list);
        Assert.NotNull(screen);
        Assert.Equal(0, screen!.Index);
    }
}
