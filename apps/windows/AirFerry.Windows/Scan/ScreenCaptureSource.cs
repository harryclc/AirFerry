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

    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);

    private readonly string _selection;
    private readonly ScreenCaptureSettings _settings;
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

    public ScreenCaptureSource(string selection, ScreenCaptureSettings settings)
    {
        _selection = string.IsNullOrWhiteSpace(selection) ? "primary" : selection.Trim();
        _settings = settings ?? ScreenCaptureSettings.Default;
    }

    public ScreenCaptureSource(string selection)
        : this(selection, ScreenCaptureSettings.Default)
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

    public Mat? ReadGray()
    {
        if (_disposed)
        {
            return null;
        }

        EnsureInitialized();
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
            _lastError ??= "初始化 DXGI 捕获失败（显示器不可用或图形适配器不支持）";
            _stats.OnUnavailable();
            _unavailable = true;
            CleanupDeviceResources();
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
                _lastError = $"DuplicateOutput 失败: {ex.Message}";
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
        if (_staging is not null && _gray is not null && Width == width && Height == height)
        {
            return;
        }

        if (width <= 0 || height <= 0 || _device is null)
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
