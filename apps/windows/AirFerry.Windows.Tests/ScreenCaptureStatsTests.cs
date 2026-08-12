using AirFerry.Windows.Scan;
using Xunit;

namespace AirFerry.Windows.Tests;

public sealed class ScreenCaptureStatsTests
{
    [Fact]
    public void NextSequence_IncrementsMonotonically()
    {
        var stats = new ScreenCaptureStats();
        Assert.Equal(1UL, stats.NextSequence());
        Assert.Equal(2UL, stats.NextSequence());
        Assert.Equal(3UL, stats.NextSequence());
    }

    [Fact]
    public void OnCaptured_IncrementsCount()
    {
        var stats = new ScreenCaptureStats();
        stats.OnCaptured();
        stats.OnCaptured();
        Assert.Equal(2, stats.Captured);
    }

    [Fact]
    public void ErrorAndRestartCounters_RecordEvents()
    {
        var stats = new ScreenCaptureStats();
        stats.OnAccessLost();
        stats.OnRestart();
        stats.OnUnavailable();
        Assert.Equal(1, stats.AccessLostCount);
        Assert.Equal(1, stats.RestartCount);
        Assert.Equal(1, stats.UnavailableCount);
    }

    [Fact]
    public void Reset_ClearsAllCounters()
    {
        var stats = new ScreenCaptureStats();
        stats.OnCaptured();
        stats.OnAccessLost();
        stats.NextSequence();
        stats.Reset();
        Assert.Equal(0, stats.Captured);
        Assert.Equal(0, stats.AccessLostCount);
        Assert.Equal(1UL, stats.NextSequence());
    }
}
