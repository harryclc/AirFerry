using System.Diagnostics;
using System.Runtime.InteropServices;
using AirFerry.Windows.Scan;
using OpenCvSharp;

namespace AirFerry.Windows.ScreenCaptureProbe;

/// <summary>
/// DXGI 屏幕捕获独立探测工具（Phase 1）：捕获所选显示器 N 秒，输出
/// FPS / 分辨率 / 帧数 / 重复帧比例（估算），可选 --save-frame 保存一帧截图。
/// 用法：ScreenCaptureProbe [--screen primary|N] [--seconds N] [--save-frame path.png]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string selection = "primary";
        int seconds = 10;
        string? saveFrame = null;
        string? logPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--screen" when i + 1 < args.Length:
                    selection = args[++i];
                    break;
                case "--seconds" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out seconds) || seconds <= 0)
                    {
                        seconds = 10;
                    }
                    break;
                case "--save-frame" when i + 1 < args.Length:
                    saveFrame = args[++i];
                    break;
                case "--log" when i + 1 < args.Length:
                    logPath = args[++i];
                    break;
                default:
                    PrintUsage();
                    return 2;
            }
        }

        if (logPath is not null)
        {
            string fullLog = Path.GetFullPath(logPath);
            string? logDir = Path.GetDirectoryName(fullLog);
            if (!string.IsNullOrEmpty(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
            var writer = new StreamWriter(fullLog) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
        }

        IReadOnlyList<ScreenInfo> screens = ScreenEnumerator.Enumerate();
        Console.WriteLine($"检测到 {screens.Count} 个显示器:");
        foreach (ScreenInfo s in screens)
        {
            Console.WriteLine($"  [{s.Index}] {s}");
        }

        ScreenInfo? target = ScreenSelector.Resolve(selection, screens);
        if (target is null)
        {
            Console.WriteLine($"未找到显示器: {selection}");
            return 1;
        }
        Console.WriteLine($"捕获目标: {target}");
        if (target.RotationDegrees != 0)
        {
            Console.WriteLine($"警告: 显示器旋转 {target.RotationDegrees}°，V1 按原始 surface 捕获（不支持旋转）");
        }

        using var source = new ScreenCaptureSource(target.DeviceName);
        long start = Stopwatch.GetTimestamp();
        long deadline = start + (long)Stopwatch.Frequency * seconds;
        ulong frames = 0;
        int duplicateSamples = 0;
        int duplicated = 0;
        byte[]? prevSignature = null;
        bool saved = false;
        int nullStreak = 0;

        while (Stopwatch.GetTimestamp() < deadline)
        {
            Mat? gray = source.ReadGray();
            if (gray is null)
            {
                nullStreak++;
                if (nullStreak > 40)
                {
                    Console.WriteLine("警告: 连续无帧，显示器可能不可用（Secure Desktop/锁屏/未连接）");
                    nullStreak = 0;
                }
                Thread.Sleep(5);
                continue;
            }
            nullStreak = 0;
            frames++;

            // 每 30 帧对降采样灰度指纹做一次重复帧估算（仅信息性，不影响解码提交）。
            if (frames % 30 == 0)
            {
                byte[] sig = ComputeSignature(gray);
                if (prevSignature is not null && sig.AsSpan().SequenceEqual(prevSignature))
                {
                    duplicated++;
                }
                prevSignature = sig;
                duplicateSamples++;
            }

            if (saveFrame is not null && !saved)
            {
                using Mat bgr = new Mat(gray.Rows, gray.Cols, MatType.CV_8UC3);
                Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
                if (Cv2.ImWrite(saveFrame, bgr))
                {
                    Console.WriteLine($"已保存截图: {Path.GetFullPath(saveFrame)}");
                    saved = true;
                }
                else
                {
                    Console.WriteLine($"保存截图失败: {saveFrame}");
                }
            }
        }

        double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        double duplicateRatio = duplicateSamples > 0 ? duplicated * 100.0 / duplicateSamples : 0;

        Console.WriteLine();
        Console.WriteLine("--- 结果 ---");
        Console.WriteLine($"捕获帧数:        {frames}");
        Console.WriteLine($"锁定设备:        {source.SelectedDeviceName ?? "(未初始化)"}");
        Console.WriteLine($"平均 FPS:         {frames / Math.Max(0.001, elapsed):F1}");
        Console.WriteLine($"分辨率:           {source.Width}×{source.Height}");
        Console.WriteLine($"估算刷新率:       {source.EstimatedRefreshHz:F0} Hz");
        Console.WriteLine($"ACCESS_LOST:      {source.Stats.AccessLostCount}  重建: {source.Stats.RestartCount}");
        Console.WriteLine($"重复帧比例(估算): {duplicateRatio:F1}%（每 30 帧采样比对降采样灰度指纹）");

        if (frames == 0)
        {
            Console.WriteLine("未捕获到任何帧：桌面在捕获窗口内可能完全静止（Desktop Duplication 只在画面变化时出帧），");
            Console.WriteLine("或当前进程会话无可变桌面（Secure Desktop/锁屏/非交互会话）。");
            Console.WriteLine($"诊断: {source.LastError ?? "无"}");
            return 1;
        }
        return 0;
    }

    private static byte[] ComputeSignature(Mat gray)
    {
        using Mat small = new Mat();
        Cv2.Resize(gray, small, new Size(64, 36));
        int length = small.Rows * small.Cols;
        byte[] buf = new byte[length];
        Marshal.Copy(small.Data, buf, 0, length);
        return buf;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法: ScreenCaptureProbe [--screen primary|N] [--seconds N] [--save-frame path.png]");
    }
}
