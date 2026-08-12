using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AirFerry.Windows.Scan;

/// <summary>
/// 基于 DXGI Desktop Duplication 的屏幕捕获源（单显示器，可选择 primary / 设备名 / 序号）。
/// 与 <see cref="VideoCapture"/> 语义一致：<see cref="ReadGray"/> 返回复用的 CV_8UC1 灰度
/// Mat，<see cref="SnapshotBgr"/> 返回池化 BGR24 预览快照。DXGI 对象在生产者线程首次
/// <see cref="ReadGray"/> 时惰性初始化；<see cref="Dispose"/> 可在生产者退出后调用。
/// </summary>
/// <remarks>
/// 帧循环：AcquireNextFrame(1000) → CopyResource → Map → BGRA2GRAY（可选 ROI）→ Unmap →
/// ReleaseFrame。WAIT_TIMEOUT 返回 null；ACCESS_LOST 等待 200ms 后按设备名/序号重新解析并
/// 重建 duplication；所选显示器消失时进入“不可用”状态，不静默切换其他屏。
/// </remarks>
public sealed class ScreenCaptureSource : IFrameProducer
{
    private const int AcquireTimeoutMs = 1000;
    private const int RecreateDelayMs = 200;
    private const int UnavailableRetryMs = 250;
    private const int PreviewFps = 15;
    private const int GdiPollFps = 60;

    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const uint SrcCopy = 0x00CC0020;
    private const uint DibRgbColors = 0;

    private enum CaptureMode
    {
        DesktopDuplication,
        GdiBitBlt,
    }

    private readonly string _selection;
    private readonly ScreenCaptureSettings _settings;
    private readonly bool _forceGdiFallback;
    private readonly ScreenCaptureStats _stats = new();

    private IDXGIFactory1? _factory;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;

    private Mat? _gray;
    private Mat? _roiView;
    private Mat? _bgrPreview;
    private bool _previewFresh;
    private long _nextPreviewAt;

    private string? _selectedDeviceName;
    private bool _initialized;
    private bool _unavailable;
    private bool _disposed;
    private long _lastPresentTime;
    private double _estimatedRefreshHz;
    private string? _lastError;
    private CaptureMode _mode = CaptureMode.DesktopDuplication;
    private string? _adapterName;
    private string? _fallbackReason;
    private int _screenLeft;
    private int _screenTop;
    private long _nextPollAt;
    private long _nextGdiResizeCheckAt;
    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _dibSection;
    private IntPtr _oldBitmap;
    private IntPtr _dibBits;

    public ScreenCaptureSource(string selection, ScreenCaptureSettings settings, bool forceGdiFallback = false)
    {
        _selection = string.IsNullOrWhiteSpace(selection) ? "primary" : selection.Trim();
        _settings = settings ?? ScreenCaptureSettings.Default;
        _forceGdiFallback = forceGdiFallback;
    }

    public ScreenCaptureSource(string selection, bool forceGdiFallback = false)
        : this(selection, ScreenCaptureSettings.Default, forceGdiFallback)
    {
    }

    public bool IsOpen => !_disposed;
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>当前锁定显示器的设备名（如 \\.\DISPLAY1），未初始化时为 null。</summary>
    public string? SelectedDeviceName => _selectedDeviceName;

    /// <summary>捕获统计（序号、计数、FPS）。</summary>
    public ScreenCaptureStats Stats => _stats;

    /// <summary>由 frameInfo.LastPresentTime 估算的显示器刷新率（Hz），未知为 0。</summary>
    public double EstimatedRefreshHz => _estimatedRefreshHz;

    /// <summary>最近一次初始化/恢复失败的诊断信息；无失败为 null。</summary>
    public string? LastError => _lastError;

    /// <summary>当前捕获模式：DXGI Desktop Duplication 或 GDI BitBlt（兼容回退）。</summary>
    public string CaptureModeName =>
        _mode == CaptureMode.GdiBitBlt ? "GDI BitBlt（兼容回退）" : "DXGI Desktop Duplication";

    /// <summary>是否处于 GDI BitBlt 兼容回退模式。</summary>
    public bool IsFallbackMode => _mode == CaptureMode.GdiBitBlt;

    /// <summary>初始化时使用的图形适配器名称（诊断用）。</summary>
    public string? AdapterName => _adapterName;

    /// <summary>触发 GDI 回退的原始 DXGI 失败原因；DXGI 模式为 null。</summary>
    public string? FallbackReason => _fallbackReason;

    public Mat? ReadGray()
    {
        if (_disposed)
        {
            return null;
        }

        EnsureInitialized();
        if (_mode == CaptureMode.GdiBitBlt)
        {
            return ReadGrayGdi();
        }
        if (_unavailable || _duplication is null || _context is null || _staging is null)
        {
            Thread.Sleep(UnavailableRetryMs);
            return null;
        }

        Result acquire = _duplication.AcquireNextFrame(
            (uint)AcquireTimeoutMs, out OutduplFrameInfo frameInfo,
            out IDXGIResource? desktopResource);
        if (acquire.Code == DxgiErrorWaitTimeout)
        {
            return null;
        }
        if (acquire.Code == DxgiErrorAccessLost)
        {
            _stats.OnAccessLost();
            RecreateDuplication();
            return null;
        }
        if (acquire.Failure || desktopResource is null)
        {
            desktopResource?.Dispose();
            _stats.OnAccessLost();
            RecreateDuplication();
            return null;
        }

        try
        {
            using ID3D11Texture2D desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            Texture2DDescription desc = desktopTexture.Description;
            EnsureTargets((int)desc.Width, (int)desc.Height);
            if (_staging is null)
            {
                return null;
            }

            _context.CopyResource(_staging, desktopTexture);

            MappedSubresource mapped = MapStaging(_context, _staging);
            try
            {
                using Mat bgra = new Mat(Height, Width, MatType.CV_8UC4,
                    mapped.DataPointer, mapped.RowPitch);
                Cv2.CvtColor(bgra, _gray!, ColorConversionCodes.BGRA2GRAY);

                long now = Stopwatch.GetTimestamp();
                if (now >= _nextPreviewAt)
                {
                    Cv2.CvtColor(bgra, _bgrPreview!, ColorConversionCodes.BGRA2BGR);
                    _previewFresh = true;
                    _nextPreviewAt = now + Stopwatch.Frequency / PreviewFps;
                }
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }

            UpdateRefreshEstimate(frameInfo.LastPresentTime);
            _stats.OnCaptured();
            _stats.NextSequence();
            return _roiView ?? _gray;
        }
        catch (Exception)
        {
            // 单帧转换失败不应终止会话；ReleaseFrame 在 finally 中保证。
            return null;
        }
        finally
        {
            _duplication.ReleaseFrame();
            desktopResource.Dispose();
        }
    }

    /// <summary>复制最近一帧的 BGR24 快照供 UI 预览；无新快照时返回 null。</summary>
    public PreviewFrame? SnapshotBgr()
    {
        if (_disposed || _bgrPreview is null || _bgrPreview.Empty() || !_previewFresh)
        {
            return null;
        }

        int width = _bgrPreview.Width;
        int height = _bgrPreview.Height;
        int rowBytes = checked(width * 3);
        int length = checked(rowBytes * height);
        int sourceStride = checked((int)_bgrPreview.Step());
        if (sourceStride < rowBytes)
        {
            return null;
        }

        byte[] pixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            if (sourceStride == rowBytes)
            {
                Marshal.Copy(_bgrPreview.Data, pixels, 0, length);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(_bgrPreview.Data, checked(y * sourceStride)),
                        pixels, checked(y * rowBytes), rowBytes);
                }
            }
            _previewFresh = false;
            return new PreviewFrame(pixels, width, height, rowBytes, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // 释放顺序：duplication → staging → context → device → factory。
        _duplication?.Dispose();
        _duplication = null;
        _staging?.Dispose();
        _staging = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _factory?.Dispose();
        _factory = null;

        GdiCleanup();

        _gray?.Dispose();
        _gray = null;
        _roiView?.Dispose();
        _roiView = null;
        _bgrPreview?.Dispose();
        _bgrPreview = null;
    }

    private void EnsureInitialized()
    {
        if (_initialized || _disposed)
        {
            return;
        }

        IReadOnlyList<ScreenInfo> screens = ScreenEnumerator.Enumerate();
        if (screens.Count == 0)
        {
            _stats.OnUnavailable();
            _unavailable = true;
            return;
        }

        ScreenInfo? screen = ScreenSelector.Resolve(_selection, screens);
        if (screen is null)
        {
            _stats.OnUnavailable();
            _unavailable = true;
            return;
        }

        if (_forceGdiFallback)
        {
            _adapterName ??= screen.AdapterName;
            _fallbackReason ??= "强制 --gdi 模式";
            if (TryInitGdiFallback(screen))
            {
                _mode = CaptureMode.GdiBitBlt;
                _selectedDeviceName = screen.DeviceName;
                Width = screen.Width;
                Height = screen.Height;
                _initialized = true;
                _unavailable = false;
            }
            else
            {
                _lastError ??= "GDI 捕获初始化失败（显示器不可用）";
                _stats.OnUnavailable();
                _unavailable = true;
            }
            return;
        }

        if (TryInitialize(screen))
        {
            _selectedDeviceName = screen.DeviceName;
            Width = screen.Width;
            Height = screen.Height;
            _initialized = true;
            _unavailable = false;
        }
        else
        {
            CleanupDeviceResources();
            string? dxgiReason = _lastError;
            if (TryInitGdiFallback(screen))
            {
                _mode = CaptureMode.GdiBitBlt;
                _fallbackReason = dxgiReason;
                _selectedDeviceName = screen.DeviceName;
                Width = screen.Width;
                Height = screen.Height;
                _initialized = true;
                _unavailable = false;
            }
            else
            {
                _lastError ??= "初始化 DXGI/GDI 捕获失败（显示器不可用或图形适配器不支持）";
                _stats.OnUnavailable();
                _unavailable = true;
            }
        }
    }

    private bool TryInitialize(ScreenInfo screen)
    {
        try
        {
            _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIAdapter1? adapter = null;
            try
            {
                Result enumResult = _factory.EnumAdapters1(0, out adapter);
                if (enumResult.Failure)
                {
                    adapter = null;
                }
            }
            catch
            {
                adapter = null;
            }
            if (adapter is null)
            {
                _lastError = "未找到可用的 DXGI 适配器";
                return false;
            }
            try
            {
                _adapterName = adapter.Description1.Description;
            }
            catch
            {
                _adapterName = null;
            }

            // 遍历 adapter 找到目标输出；找不到则释放并失败。
            IDXGIOutput? targetOutput = null;
            int adapterIndex = 0;
            while (adapter is not null)
            {
                int outputIndex = 0;
                while (true)
                {
                    IDXGIOutput? output;
                    try
                    {
                        Result outputResult = adapter.EnumOutputs((uint)outputIndex, out output);
                        if (outputResult.Failure)
                        {
                            break;
                        }
                    }
                    catch
                    {
                        break;
                    }
                    if (output is null)
                    {
                        break;
                    }
                    if (output.Description.AttachedToDesktop &&
                        string.Equals(output.Description.DeviceName, screen.DeviceName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        targetOutput = output;
                        break;
                    }
                    output.Dispose();
                    outputIndex++;
                }

                if (targetOutput is not null)
                {
                    break;
                }
                adapter.Dispose();
                adapterIndex++;
                try
                {
                    Result enumResult = _factory.EnumAdapters1((uint)adapterIndex, out adapter);
                    if (enumResult.Failure)
                    {
                        adapter = null;
                    }
                }
                catch
                {
                    adapter = null;
                }
            }

            if (targetOutput is null)
            {
                _lastError = $"未找到所选显示器输出: {screen.DeviceName}";
                adapter?.Dispose();
                return false;
            }

            Result createResult = D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out _device,
                out _context);
            if (createResult.Failure || _device is null || _context is null)
            {
                _lastError = $"D3D11 设备创建失败: 0x{createResult.Code:X8}";
                targetOutput.Dispose();
                adapter?.Dispose();
                return false;
            }

            using IDXGIOutput1 output1 = targetOutput.QueryInterface<IDXGIOutput1>();
            try
            {
                _duplication = output1.DuplicateOutput(_device);
            }
            catch (Exception ex)
            {
                _lastError = $"DuplicateOutput 失败（适配器 {_adapterName ?? "未知"}）: {ex.Message}";
                targetOutput.Dispose();
                adapter?.Dispose();
                return false;
            }
            if (_duplication is null)
            {
                _lastError = "DuplicateOutput 返回空";
                targetOutput.Dispose();
                adapter?.Dispose();
                return false;
            }

            targetOutput.Dispose();
            adapter?.Dispose();
            EnsureTargets(screen.Width, screen.Height);
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"D3D11/DXGI 初始化异常: {ex.Message}";
            CleanupDeviceResources();
            return false;
        }
    }

    private void RecreateDuplication()
    {
        if (_mode == CaptureMode.GdiBitBlt)
        {
            return;
        }
        CleanupDuplication();
        Thread.Sleep(RecreateDelayMs);
        _stats.OnRestart();

        IReadOnlyList<ScreenInfo> screens = ScreenEnumerator.Enumerate();
        if (screens.Count == 0)
        {
            _initialized = false;
            _unavailable = true;
            return;
        }

        // 优先按设备名重解析（稳定键），回退到选择表达式。
        ScreenInfo? screen = _selectedDeviceName is not null
            ? screens.FirstOrDefault(s => string.Equals(
                s.DeviceName, _selectedDeviceName, StringComparison.OrdinalIgnoreCase))
            : null;
        screen ??= ScreenSelector.Resolve(_selection, screens);
        if (screen is null)
        {
            _initialized = false;
            _unavailable = true;
            return;
        }

        if (TryInitialize(screen))
        {
            _selectedDeviceName = screen.DeviceName;
            _unavailable = false;
        }
        else
        {
            _initialized = false;
            _unavailable = true;
        }
    }

    private void EnsureTargets(int width, int height)
    {
        if (_gray is not null && _bgrPreview is not null && Width == width && Height == height)
        {
            return;
        }

        if (width <= 0 || height <= 0)
        {
            return;
        }

        _gray?.Dispose();
        _gray = null;
        _roiView?.Dispose();
        _roiView = null;
        _bgrPreview?.Dispose();
        _bgrPreview = null;
        _staging?.Dispose();
        _staging = null;

        if (_mode == CaptureMode.DesktopDuplication && _device is not null)
        {
            var description = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };
            _staging = _device.CreateTexture2D(description);
        }
        _gray = new Mat(height, width, MatType.CV_8UC1);
        _bgrPreview = new Mat(height, width, MatType.CV_8UC3);

        RoiRect roi = RoiRect.Clamp(_settings.ToRoiRect(), width, height);
        _roiView = roi.Enabled
            ? new Mat(_gray, new OpenCvSharp.Rect(roi.X, roi.Y, roi.Width, roi.Height))
            : null;

        Width = width;
        Height = height;
        _previewFresh = false;
    }

    private static MappedSubresource MapStaging(ID3D11DeviceContext context, ID3D11Texture2D staging)
    {
        Result result = context.Map(
            staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped);
        if (result.Failure)
        {
            throw new InvalidOperationException($"staging Map 失败: 0x{result.Code:X8}");
        }
        return mapped;
    }

    private void UpdateRefreshEstimate(long lastPresentTime)
    {
        if (lastPresentTime <= 0)
        {
            return;
        }
        if (_lastPresentTime > 0 && lastPresentTime > _lastPresentTime)
        {
            long delta = lastPresentTime - _lastPresentTime;
            if (delta > 0)
            {
                _estimatedRefreshHz = 10_000_000.0 / delta;
            }
        }
        _lastPresentTime = lastPresentTime;
    }

    private bool TryInitGdiFallback(ScreenInfo screen)
    {
        try
        {
            _screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (_screenDc == IntPtr.Zero)
            {
                _lastError = "GDI GetDC 失败";
                return false;
            }
            _memDc = NativeMethods.CreateCompatibleDC(_screenDc);
            if (_memDc == IntPtr.Zero)
            {
                _lastError = "GDI CreateCompatibleDC 失败";
                GdiCleanup();
                return false;
            }
            if (!EnsureGdiTargets(screen))
            {
                GdiCleanup();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"GDI 初始化异常: {ex.Message}";
            GdiCleanup();
            return false;
        }
    }

    private bool EnsureGdiTargets(ScreenInfo screen)
    {
        if (_dibSection != IntPtr.Zero)
        {
            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_memDc, _oldBitmap);
            }
            NativeMethods.DeleteObject(_dibSection);
            _dibSection = IntPtr.Zero;
            _dibBits = IntPtr.Zero;
        }

        var header = new NativeMethods.BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
            biWidth = screen.Width,
            biHeight = -screen.Height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        _dibSection = NativeMethods.CreateDIBSection(
            _memDc, ref header, DibRgbColors, out _dibBits, IntPtr.Zero, 0);
        if (_dibSection == IntPtr.Zero)
        {
            _lastError = "GDI CreateDIBSection 失败";
            return false;
        }
        _oldBitmap = NativeMethods.SelectObject(_memDc, _dibSection);
        _screenLeft = screen.DesktopLeft;
        _screenTop = screen.DesktopTop;
        EnsureTargets(screen.Width, screen.Height);
        return true;
    }

    private Mat? ReadGrayGdi()
    {
        long now = Stopwatch.GetTimestamp();
        long interval = Stopwatch.Frequency / GdiPollFps;
        if (now < _nextPollAt)
        {
            long waitMs = (_nextPollAt - now) * 1000 / Stopwatch.Frequency;
            Thread.Sleep((int)Math.Max(1, waitMs));
            return null;
        }
        _nextPollAt = now + interval;

        // 周期性检测所选显示器分辨率/位置变化（约 1 秒一次），变化时重建 GDI 目标。
        if (now >= _nextGdiResizeCheckAt)
        {
            _nextGdiResizeCheckAt = now + Stopwatch.Frequency;
            IReadOnlyList<ScreenInfo> screens = ScreenEnumerator.Enumerate();
            ScreenInfo? current = _selectedDeviceName is not null
                ? screens.FirstOrDefault(s => string.Equals(
                    s.DeviceName, _selectedDeviceName, StringComparison.OrdinalIgnoreCase))
                : ScreenSelector.Resolve(_selection, screens);
            if (current is null)
            {
                _lastError = "所选显示器不可用（GDI 模式）";
                _initialized = false;
                _unavailable = true;
                return null;
            }
            if (current.Width != Width || current.Height != Height ||
                current.DesktopLeft != _screenLeft || current.DesktopTop != _screenTop)
            {
                EnsureGdiTargets(current);
            }
        }

        if (_screenDc == IntPtr.Zero || _memDc == IntPtr.Zero ||
            _dibBits == IntPtr.Zero || _gray is null)
        {
            _unavailable = true;
            return null;
        }

        if (!NativeMethods.BitBlt(
                _memDc, 0, 0, Width, Height, _screenDc, _screenLeft, _screenTop, SrcCopy))
        {
            _lastError = "GDI BitBlt 失败";
            return null;
        }

        using Mat bgra = new Mat(Height, Width, MatType.CV_8UC4, _dibBits, Width * 4L);
        Cv2.CvtColor(bgra, _gray, ColorConversionCodes.BGRA2GRAY);
        if (now >= _nextPreviewAt)
        {
            Cv2.CvtColor(bgra, _bgrPreview!, ColorConversionCodes.BGRA2BGR);
            _previewFresh = true;
            _nextPreviewAt = now + Stopwatch.Frequency / PreviewFps;
        }
        _stats.OnCaptured();
        _stats.NextSequence();
        return _roiView ?? _gray;
    }

    private void GdiCleanup()
    {
        if (_dibSection != IntPtr.Zero)
        {
            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_memDc, _oldBitmap);
            }
            NativeMethods.DeleteObject(_dibSection);
            _dibSection = IntPtr.Zero;
            _dibBits = IntPtr.Zero;
        }
        if (_memDc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        if (_screenDc != IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
            _screenDc = IntPtr.Zero;
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(
            IntPtr hdc, ref BitmapInfoHeader pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(
            IntPtr hdcDest, int xDest, int yDest, int width, int height,
            IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);
    }

    private void CleanupDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
    }

    private void CleanupDeviceResources()
    {
        CleanupDuplication();
        _staging?.Dispose();
        _staging = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _factory?.Dispose();
        _factory = null;
    }
}
