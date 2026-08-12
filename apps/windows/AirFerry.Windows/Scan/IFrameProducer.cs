using OpenCvSharp;

namespace AirFerry.Windows.Scan;

/// <summary>
/// 帧生产者抽象：相机/采集卡（<see cref="VideoCapture"/>）与屏幕捕获
/// （<see cref="ScreenCaptureSource"/>）共用。语义与现有 VideoCapture 完全一致：
/// <see cref="ReadGray"/> 返回对象内复用的 CV_8UC1 灰度 Mat（调用方需在下次读取前
/// 使用完）；<see cref="SnapshotBgr"/> 返回池化 BGR24 预览快照。两者都只允许在
/// 生产者线程调用。
/// </summary>
public interface IFrameProducer : IDisposable
{
    /// <summary>源是否可用（未释放）。</summary>
    bool IsOpen { get; }

    /// <summary>实际帧宽度（首帧读取前为 0）。</summary>
    int Width { get; }

    /// <summary>实际帧高度（首帧读取前为 0）。</summary>
    int Height { get; }

    /// <summary>
    /// 读取一帧并转换为灰度。返回 null 表示暂无新帧（超时）或设备暂不可用，
    /// 调用方应稍后重试；重复 null 需结合具体源判断是否致命。
    /// </summary>
    Mat? ReadGray();

    /// <summary>复制最近一帧的 BGR24 快照供 UI 预览；无新帧时返回 null。</summary>
    PreviewFrame? SnapshotBgr();
}
