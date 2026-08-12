namespace AirFerry.Windows.Scan;

/// <summary>
/// 单个已连接显示器（DXGI 输出）的信息。纯逻辑类型，供枚举、选择与 UI 展示共用。
/// </summary>
public sealed record ScreenInfo(
    /// <summary>DXGI 枚举序号（0 起）。</summary>
    int Index,
    /// <summary>设备名（如 \\.\DISPLAY1），作为重枚举时的稳定匹配键。</summary>
    string DeviceName,
    /// <summary>当前分辨率宽度。</summary>
    int Width,
    /// <summary>当前分辨率高度。</summary>
    int Height,
    /// <summary>桌面坐标 Left。</summary>
    int DesktopLeft,
    /// <summary>桌面坐标 Top。</summary>
    int DesktopTop,
    /// <summary>是否主显示器（桌面坐标包含 (0,0)）。</summary>
    bool IsPrimary,
    /// <summary>旋转角度：0/90/180/270。</summary>
    int RotationDegrees)
{
    /// <summary>桌面坐标 Right（Left + Width）。</summary>
    public int DesktopRight => DesktopLeft + Width;

    /// <summary>桌面坐标 Bottom（Top + Height）。</summary>
    public int DesktopBottom => DesktopTop + Height;

    public override string ToString()
    {
        string primary = IsPrimary ? " · 主显示器" : string.Empty;
        string rotation = RotationDegrees == 0 ? string.Empty : $" · 旋转 {RotationDegrees}°";
        return $"屏幕 {Index + 1}{primary} · {Width}×{Height}{rotation}";
    }
}

/// <summary>
/// 显示器选择解析：支持 "primary"、设备名（\\.\DISPLAYx）与枚举序号（"0"/"1"...）。
/// 纯逻辑，可在任意平台单测。
/// </summary>
public static class ScreenSelector
{
    /// <summary>按选择字符串解析出 ScreenInfo；找不到时返回 null。</summary>
    public static ScreenInfo? Resolve(string selection, IReadOnlyList<ScreenInfo> screens)
    {
        if (screens.Count == 0)
        {
            return null;
        }

        string key = (selection ?? "primary").Trim();
        if (key.Length == 0)
        {
            key = "primary";
        }

        if (string.Equals(key, "primary", StringComparison.OrdinalIgnoreCase))
        {
            return screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];
        }

        // 设备名匹配（稳定键）。
        ScreenInfo? byName = screens.FirstOrDefault(
            s => string.Equals(s.DeviceName, key, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        // 枚举序号匹配。
        if (int.TryParse(key, out int index) && index >= 0 && index < screens.Count)
        {
            return screens[index];
        }

        return null;
    }
}
