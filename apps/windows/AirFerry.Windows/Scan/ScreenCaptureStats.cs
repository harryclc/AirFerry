using System.Diagnostics;

namespace AirFerry.Windows.Scan;

/// <summary>
/// 屏幕捕获统计：帧序号、计数与 1 秒窗口 FPS。纯逻辑，可跨平台单测。
/// 采集/提交/丢弃的最终计数仍以 <see cref="QrDecodePool"/> 为准。
/// </summary>
public sealed class ScreenCaptureStats
{
    private const long FpsWindowTicks = 1_000_0000; // 1 秒（Stopwatch.Frequency 单位为 100ns）

    private readonly Queue<long> _frameTicks = new();
    private long _sequence;
    private long _captured;
    private long _accessLost;
    private long _restarts;
    private long _unavailable;

    /// <summary>取下一帧序号（从 1 开始单调递增）。</summary>
    public ulong NextSequence() => (ulong)Interlocked.Increment(ref _sequence);

    /// <summary>已捕获帧数。</summary>
    public long Captured => Interlocked.Read(ref _captured);

    /// <summary>ACCESS_LOST 次数。</summary>
    public long AccessLostCount => Interlocked.Read(ref _accessLost);

    /// <summary>重建 duplication 次数。</summary>
    public long RestartCount => Interlocked.Read(ref _restarts);

    /// <summary>所选显示器不可用观测次数（信息性）。</summary>
    public long UnavailableCount => Interlocked.Read(ref _unavailable);

    /// <summary>记录一次捕获成功（含时间戳用于 FPS 窗口）。</summary>
    public void OnCaptured()
    {
        Interlocked.Increment(ref _captured);
        lock (_frameTicks)
        {
            _frameTicks.Enqueue(Stopwatch.GetTimestamp());
        }
    }

    /// <summary>记录 ACCESS_LOST。</summary>
    public void OnAccessLost() => Interlocked.Increment(ref _accessLost);

    /// <summary>记录一次 duplication 重建。</summary>
    public void OnRestart() => Interlocked.Increment(ref _restarts);

    /// <summary>记录一次“显示器不可用”观测。</summary>
    public void OnUnavailable() => Interlocked.Increment(ref _unavailable);

    /// <summary>最近 1 秒内的捕获帧数（≈ 当前 FPS）。</summary>
    public double CaptureFps
    {
        get
        {
            lock (_frameTicks)
            {
                long now = Stopwatch.GetTimestamp();
                long cutoff = now - FpsWindowTicks;
                while (_frameTicks.Count > 0 && _frameTicks.Peek() < cutoff)
                {
                    _frameTicks.Dequeue();
                }
                return _frameTicks.Count;
            }
        }
    }

    /// <summary>重置全部统计（新会话）。</summary>
    public void Reset()
    {
        lock (_frameTicks)
        {
            _frameTicks.Clear();
        }
        Interlocked.Exchange(ref _sequence, 0);
        Interlocked.Exchange(ref _captured, 0);
        Interlocked.Exchange(ref _accessLost, 0);
        Interlocked.Exchange(ref _restarts, 0);
        Interlocked.Exchange(ref _unavailable, 0);
    }
}
