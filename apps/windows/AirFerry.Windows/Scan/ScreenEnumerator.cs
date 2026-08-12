using SharpGen.Runtime;
using Vortice.DXGI;

namespace AirFerry.Windows.Scan;

/// <summary>
/// DXGI 显示器枚举：遍历所有 adapter 的已连接输出，构造 <see cref="ScreenInfo"/> 列表。
/// 主显示器判定：桌面坐标包含 (0,0) 的输出。
/// </summary>
public static class ScreenEnumerator
{
    /// <summary>枚举所有已连接显示器；失败时返回空列表（不抛异常）。</summary>
    public static IReadOnlyList<ScreenInfo> Enumerate()
    {
        var list = new List<ScreenInfo>();
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            int adapterIndex = 0;
            while (TryGetAdapter(factory, adapterIndex, out IDXGIAdapter1? adapter) &&
                   adapter is not null)
            {
                using (adapter)
                {
                    int outputIndex = 0;
                    while (TryGetOutput(adapter, outputIndex, out IDXGIOutput? output) &&
                           output is not null)
                    {
                        using (output)
                        {
                            OutputDescription desc = output.Description;
                            if (!desc.AttachedToDesktop)
                            {
                                outputIndex++;
                                continue;
                            }

                            int left = desc.DesktopCoordinates.Left;
                            int top = desc.DesktopCoordinates.Top;
                            int width = Math.Max(0, desc.DesktopCoordinates.Right - left);
                            int height = Math.Max(0, desc.DesktopCoordinates.Bottom - top);
                            if (width == 0 || height == 0)
                            {
                                outputIndex++;
                                continue;
                            }

                            bool primary = left <= 0 && top <= 0 &&
                                           desc.DesktopCoordinates.Right > 0 &&
                                           desc.DesktopCoordinates.Bottom > 0;
                            int rotation = desc.Rotation switch
                            {
                                ModeRotation.Rotate90 => 90,
                                ModeRotation.Rotate180 => 180,
                                ModeRotation.Rotate270 => 270,
                                _ => 0,
                            };

                            list.Add(new ScreenInfo(
                                Index: list.Count,
                                DeviceName: desc.DeviceName,
                                Width: width,
                                Height: height,
                                DesktopLeft: left,
                                DesktopTop: top,
                                IsPrimary: primary,
                                RotationDegrees: rotation));
                        }
                        outputIndex++;
                    }
                }
                adapterIndex++;
            }
        }
        catch
        {
            // 无 DXGI / 无显示器 → 空列表，由调用方展示“捕获不可用”。
        }
        return list;
    }

    private static bool TryGetAdapter(IDXGIFactory1 factory, int index, out IDXGIAdapter1? adapter)
    {
        try
        {
            Result result = factory.EnumAdapters1((uint)index, out adapter);
            return result.Success && adapter is not null;
        }
        catch
        {
            adapter = null;
            return false;
        }
    }

    private static bool TryGetOutput(IDXGIAdapter1 adapter, int index, out IDXGIOutput? output)
    {
        try
        {
            Result result = adapter.EnumOutputs((uint)index, out output);
            return result.Success && output is not null;
        }
        catch
        {
            output = null;
            return false;
        }
    }
}
