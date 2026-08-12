using AirFerry.Windows.Scan;

namespace AirFerry.Windows.Models;

/// <summary>
/// A video-input device surfaced by DirectShow. Webcams and capture cards
/// (USB / HDMI / SDI) are indistinguishable at this layer — they all show up as
/// <c>FilterCategory.VideoInputDevice</c> — so the same enumeration drives both.
/// </summary>
public sealed record DeviceInfo(
    /// <summary>Human-readable name for the dropdown (e.g. "Logitech C920").</summary>
    string FriendlyName,
    /// <summary>
    /// DirectShow device moniker string — the opaque handle OpenCvSharp's
    /// <c>VideoCapture</c> accepts via its DirectShow backend to bind the
    /// specific device (rather than a 0-based index, which renumbers when
    /// devices are plugged/unplugged).
    /// </summary>
    string MonikerString,
    /// <summary>
    /// 0-based DirectShow device index — the legacy way to open a device. Kept
    /// as a fallback because some drivers don't round-trip a moniker cleanly.
    /// </summary>
    int Index,
    /// <summary>
    /// Heuristic guess (not authoritative): true if the name looks like a
    /// capture card rather than a webcam. Surfaced in the UI so the user can
    /// tell at a glance, but the device behaves identically either way.
    /// </summary>
    bool IsCaptureCard,
    /// <summary>输入类型：摄像头/采集卡或屏幕捕获。</summary>
    InputKind Kind = InputKind.Camera)
{
    public override string ToString()
    {
        if (Kind == InputKind.Screen)
        {
            return FriendlyName;
        }
        if (IsCaptureCard)
        {
            return $"{FriendlyName} (采集卡)";
        }
        return FriendlyName;
    }
}
