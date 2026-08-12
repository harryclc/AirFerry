namespace AirFerry.Windows.Scan;

/// <summary>
/// ROI 矩形（相对捕获帧左上角）。纯逻辑类型，可跨平台单测。
/// </summary>
public readonly record struct RoiRect(int X, int Y, int Width, int Height)
{
    /// <summary>宽高均大于 0 时视为启用；否则表示整帧。</summary>
    public bool Enabled => Width > 0 && Height > 0;

    /// <summary>
    /// 钳制到帧边界：越界部分截断，宽高取与帧的交集；未启用或帧尺寸非法时返回整帧。
    /// </summary>
    public static RoiRect Clamp(RoiRect roi, int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || !roi.Enabled)
        {
            return new RoiRect(0, 0, frameWidth, frameHeight);
        }

        int x = Math.Clamp(roi.X, 0, Math.Max(0, frameWidth - 1));
        int y = Math.Clamp(roi.Y, 0, Math.Max(0, frameHeight - 1));
        int right = Math.Clamp(roi.X + roi.Width, x, frameWidth);
        int bottom = Math.Clamp(roi.Y + roi.Height, y, frameHeight);
        return new RoiRect(x, y, right - x, bottom - y);
    }
}
