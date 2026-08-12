# AirFerry Windows 屏幕捕获（DXGI Desktop Duplication）

> 对应任务说明：`D:\AirFerry-ScreenCapture\AGENTS.md`（Screen Capture 开发任务）。
> 本页记录 Windows 接收端屏幕捕获输入源的接口、配置、统计、错误恢复与验收方法。

## 1. 目标与边界

- 通过 DXGI Desktop Duplication 捕获本地 Windows 显示器最终像素，从 FusionAccess 全屏云桌面中读取 AirFerry 二维码视频。
- 支持用户选择单个显示器（`primary` / 设备名 `\\.\DISPLAYx` / 枚举序号），**不做多显示器拼接/跨屏时间戳同步**。
- 不解析 FusionAccess/HDP 协议、不依赖 OBS/虚拟摄像头；不修改 QR 解码、RaptorQ、文件恢复与 `QrDecodePool` 调度状态机。

## 2. 接口

```text
IFrameProducer（Scan/IFrameProducer.cs）
├── VideoCapture          （现有 DirectShow 相机/采集卡源）
└── ScreenCaptureSource   （DXGI Desktop Duplication）
```

- `Mat? ReadGray()`：CV_8UC1 灰度帧（对象内复用）；WAIT_TIMEOUT 或暂不可用时返回 null。
- `PreviewFrame? SnapshotBgr()`：≤15fps 池化 BGR24 预览快照。
- `InputDescriptor(Kind, DeviceIndex, ScreenSelection)`：扫码页输入描述，由设备选择页构造。
- `ScreenCaptureSource(string selection, ScreenCaptureSettings settings)`：selection 为 `primary` / 设备名 / 序号。

## 3. 配置（设置页 → `%AppData%\AirFerry\settings.json`）

```json
{
  "default_redundancy": 5,
  "screen_capture": {
    "roi_enabled": false,
    "roi_x": 0,
    "roi_y": 0,
    "roi_width": 1920,
    "roi_height": 1080
  }
}
```

- ROI 默认关闭（整帧 → 解码）；开启后按坐标裁剪并自动钳制到显示器边界。
- 显示器选择为会话级，不持久化。

## 4. 统计

```text
captured_frames / sequence_number / capture_fps（1 秒窗口）
access_lost_count / restart_count / unavailable_count
分辨率 / 估算刷新率（由 LastPresentTime 推算）
```

采集/提交/丢弃的最终计数以 `QrDecodePool`（CapturedFrames/DroppedFrames/DecodedSymbols）为准。

## 5. 错误恢复

- `DXGI_ERROR_ACCESS_LOST`：释放 duplication → 等待 200ms → 重新枚举 → 按设备名优先重解析 → 重建；绝不退出接收端。
- `DXGI_ERROR_UNSUPPORTED`（适配器不支持 Desktop Duplication，常见于未装显卡驱动/基本显示适配器/虚拟机无 3D 加速/远程桌面会话）：
  自动回退到 **GDI BitBlt** 兼容捕获（约 60 FPS 轮询 + 1 秒一次分辨率变化检测），界面会标注“GDI 回退”；DXGI 失败原因保留在 `FallbackReason`。
- 所选显示器消失：进入“显示器不可用”状态并提示，不静默切换其他屏；显示器恢复后自动重试。
- 分辨率变化：重建 staging texture 与灰度/预览 Mat。
- `Rotation != 0`：记录 warning，V1 按原始 surface 捕获。
- Secure Desktop（UAC/锁屏/登录屏）可能无法捕获：返回“捕获不可用”，不崩溃。

## 6. 探测工具（ScreenCaptureProbe）

```text
ScreenCaptureProbe [--screen primary|N] [--seconds N] [--save-frame path.png] [--log path.log]
```

输出：显示器列表、锁定设备、捕获模式（DXGI / GDI 回退）、适配器名称、FPS、分辨率、估算刷新率、ACCESS_LOST/重建次数、重复帧比例（每 30 帧采样比对降采样灰度指纹，信息性估算）。

## 7. 验收

- 功能：显示器枚举与选择、DXGI 捕获、QR 解码、RaptorQ 恢复、文件重建。
- 稳定性：连续 ≥1 小时无崩溃/内存或 GPU 泄漏/永久冻结；FusionAccess 重连、分辨率变化、锁屏后自动恢复。
- 性能：1920×1080@60Hz 目标捕获 ≥50 FPS、CPU < 单核 20%、QR 成功率 > 95%（以 Effective QR Symbol Rate 评估）。
- FusionAccess 场景矩阵：全屏进入/退出、断线重连、网络瞬断、分辨率切换、锁屏、注销、显示器拔插。
