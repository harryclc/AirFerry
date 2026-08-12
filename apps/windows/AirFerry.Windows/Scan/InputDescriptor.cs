namespace AirFerry.Windows.Scan;

/// <summary>扫码输入类型。</summary>
public enum InputKind
{
    /// <summary>摄像头 / 采集卡（DirectShow，按 0 基序号打开）。</summary>
    Camera,

    /// <summary>屏幕捕获（DXGI Desktop Duplication）。</summary>
    Screen,
}

/// <summary>
/// 扫码页输入描述：相机（DirectShow 设备序号）或屏幕（"primary" / 设备名 / 枚举序号）。
/// 由设备选择页构造，传递给 ScanView → ScanViewModel。
/// </summary>
public sealed record InputDescriptor(
    InputKind Kind,
    int DeviceIndex,
    string ScreenSelection)
{
    public static InputDescriptor Camera(int deviceIndex) =>
        new(InputKind.Camera, deviceIndex, string.Empty);

    public static InputDescriptor ScreenPrimary() =>
        new(InputKind.Screen, -1, "primary");

    public static InputDescriptor Screen(string selection) =>
        new(InputKind.Screen, -1, selection);
}
