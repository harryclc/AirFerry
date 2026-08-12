using AirFerry.Windows.Scan;
using Xunit;

namespace AirFerry.Windows.Tests;

public sealed class ScreenCaptureSettingsTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        (int redundancy, ScreenCaptureSettings screen) = ScreenSettingsStore.Load(path);
        Assert.Equal(5, redundancy);
        Assert.Equal(ScreenCaptureSettings.Default, screen);
        Assert.False(screen.RoiEnabled);
    }

    [Fact]
    public void SaveLoad_RoundTrip_PreservesAllFields()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var screen = new ScreenCaptureSettings(true, 10, 20, 800, 600);
        ScreenSettingsStore.Save(15, screen, path);

        (int redundancy, ScreenCaptureSettings loaded) = ScreenSettingsStore.Load(path);
        Assert.Equal(15, redundancy);
        Assert.Equal(screen, loaded);
    }

    [Fact]
    public void Load_LegacyRedundancyOnlyFile_KeepsScreenDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"default_redundancy\":20}");

        (int redundancy, ScreenCaptureSettings screen) = ScreenSettingsStore.Load(path);
        Assert.Equal(20, redundancy);
        Assert.Equal(ScreenCaptureSettings.Default, screen);
    }

    [Fact]
    public void Load_MalformedJson_ReturnsDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not valid json !!");

        (int redundancy, ScreenCaptureSettings screen) = ScreenSettingsStore.Load(path);
        Assert.Equal(5, redundancy);
        Assert.Equal(ScreenCaptureSettings.Default, screen);
    }
}
