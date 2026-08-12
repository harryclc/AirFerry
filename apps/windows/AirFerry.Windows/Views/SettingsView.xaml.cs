using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AirFerry.Windows.Scan;

namespace AirFerry.Windows.Views;

/// <summary>
/// Settings page — mirrors Android's <c>SettingsActivity</c>: a "default
/// redundancy" slider persisted to %AppData%\AirFerry\settings.json (the .NET
/// analogue of SharedPreferences), plus screen-capture ROI settings in the same
/// file, and the version read from the assembly (the single source of truth —
/// the csproj <c>&lt;Version&gt;</c>).
/// </summary>
public partial class SettingsView : Page
{
    private bool _populating;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Populate();
    }

    private void Populate()
    {
        _populating = true;
        try
        {
            (int redundancy, ScreenCaptureSettings screen) = ScreenSettingsStore.Load();
            RedundancySlider.Value = redundancy;
            RedundancyText.Text = $"{redundancy}%";
            RoiEnabledCheck.IsChecked = screen.RoiEnabled;
            RoiXBox.Text = screen.RoiX.ToString();
            RoiYBox.Text = screen.RoiY.ToString();
            RoiWidthBox.Text = screen.RoiWidth.ToString();
            RoiHeightBox.Text = screen.RoiHeight.ToString();

            // Read version from the assembly (the csproj <Version>).
            Version? ver = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = ver is not null ? $"版本 {ver.Major}.{ver.Minor}.{ver.Build}" : "版本 ?";
        }
        finally
        {
            _populating = false;
        }
    }

    private void Redundancy_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_populating)
        {
            return;
        }
        int value = (int)Math.Round(e.NewValue);
        RedundancyText.Text = $"{value}%";
        Save();
    }

    private void Roi_Changed(object sender, RoutedEventArgs e)
    {
        if (_populating)
        {
            return;
        }
        Save();
    }

    private void Save()
    {
        try
        {
            (_, ScreenCaptureSettings current) = ScreenSettingsStore.Load();
            int redundancy = (int)Math.Round(RedundancySlider.Value);
            var screen = new ScreenCaptureSettings(
                RoiEnabledCheck.IsChecked == true,
                ParseInt(RoiXBox.Text, current.RoiX),
                ParseInt(RoiYBox.Text, current.RoiY),
                ParseInt(RoiWidthBox.Text, current.RoiWidth),
                ParseInt(RoiHeightBox.Text, current.RoiHeight));
            ScreenSettingsStore.Save(redundancy, screen);
        }
        catch { /* settings are best-effort; never block the UI */ }
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, out int v) ? v : fallback;

    private void Back_Click(object sender, RoutedEventArgs e) => NavigationService?.GoBack();
}
