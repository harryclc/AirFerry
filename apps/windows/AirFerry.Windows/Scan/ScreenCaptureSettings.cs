using System.IO;
using System.Text;
using System.Text.Json;

namespace AirFerry.Windows.Scan;

/// <summary>
/// 屏幕捕获设置（ROI）。纯逻辑，可跨平台单测。持久化于
/// <c>%AppData%\AirFerry\settings.json</c>，与现有 <c>default_redundancy</c> 同文件。
/// </summary>
public sealed record ScreenCaptureSettings(
    bool RoiEnabled,
    int RoiX,
    int RoiY,
    int RoiWidth,
    int RoiHeight)
{
    /// <summary>默认：ROI 关闭（整帧 → 解码）。</summary>
    public static ScreenCaptureSettings Default { get; } = new(false, 0, 0, 1920, 1080);

    /// <summary>转换为 ROI 矩形；未启用时返回整帧语义的矩形。</summary>
    public RoiRect ToRoiRect() => RoiEnabled
        ? new RoiRect(RoiX, RoiY, RoiWidth, RoiHeight)
        : new RoiRect(0, 0, 0, 0);
}

/// <summary>
/// settings.json 读写。与现有 <c>default_redundancy</c> 共用同一文件：
/// 读取时保留旧字段，保存时写回完整对象，避免互相覆盖。
/// </summary>
public static class ScreenSettingsStore
{
    private const int DefaultRedundancy = 5;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AirFerry", "settings.json");

    /// <summary>读取设置；文件缺失或损坏时返回默认值。</summary>
    public static (int Redundancy, ScreenCaptureSettings Screen) Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
            {
                return (DefaultRedundancy, ScreenCaptureSettings.Default);
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;

            int redundancy = DefaultRedundancy;
            if (root.TryGetProperty("default_redundancy", out JsonElement r) &&
                r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out int rv))
            {
                redundancy = Math.Clamp(rv, 5, 50);
            }

            ScreenCaptureSettings screen = ScreenCaptureSettings.Default;
            if (root.TryGetProperty("screen_capture", out JsonElement sc) &&
                sc.ValueKind == JsonValueKind.Object)
            {
                screen = new ScreenCaptureSettings(
                    ReadBool(sc, "roi_enabled", false),
                    ReadInt(sc, "roi_x", 0),
                    ReadInt(sc, "roi_y", 0),
                    ReadInt(sc, "roi_width", 1920),
                    ReadInt(sc, "roi_height", 1080));
            }

            return (redundancy, screen);
        }
        catch
        {
            return (DefaultRedundancy, ScreenCaptureSettings.Default);
        }
    }

    /// <summary>保存设置（完整写回，保留冗余字段）。</summary>
    public static void Save(int redundancy, ScreenCaptureSettings screen, string? path = null)
    {
        path ??= DefaultPath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("default_redundancy", Math.Clamp(redundancy, 5, 50));
            writer.WritePropertyName("screen_capture");
            writer.WriteStartObject();
            writer.WriteBoolean("roi_enabled", screen.RoiEnabled);
            writer.WriteNumber("roi_x", screen.RoiX);
            writer.WriteNumber("roi_y", screen.RoiY);
            writer.WriteNumber("roi_width", screen.RoiWidth);
            writer.WriteNumber("roi_height", screen.RoiHeight);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        File.WriteAllText(path, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static bool ReadBool(JsonElement obj, string name, bool fallback) =>
        obj.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.True
            ? true
            : obj.TryGetProperty(name, out e) && e.ValueKind == JsonValueKind.False
                ? false
                : fallback;

    private static int ReadInt(JsonElement obj, string name, int fallback) =>
        obj.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number &&
        e.TryGetInt32(out int v)
            ? v
            : fallback;
}
