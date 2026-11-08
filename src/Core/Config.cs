using System.Text.Json;

namespace DeskTidy.Core;

/// <summary>极简配置。目前只有"收纳用的那个桌面文件夹"。</summary>
internal sealed class Config
{
    public string? AnchorPath { get; set; }

    /// <summary>悬浮球窗口左上角（屏幕物理像素）。null = 用默认位置。</summary>
    public int? BallX { get; set; }
    public int? BallY { get; set; }

    private static string PathFile => Path.Combine(AppPaths.DataDir, "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(PathFile)) ?? new Config();
        }
        catch (Exception ex) { AppLog.Warn($"读配置失败：{ex.Message}"); }
        return new Config();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Warn($"写配置失败：{ex.Message}"); }
    }

    /// <summary>没配置就挑桌面上第一个普通文件夹。</summary>
    public string ResolveAnchor()
    {
        if (!string.IsNullOrWhiteSpace(AnchorPath) && Directory.Exists(AnchorPath))
            return AnchorPath;

        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var dir = Directory.EnumerateDirectories(desktop)
                .FirstOrDefault(d => (File.GetAttributes(d) & (FileAttributes.Hidden | FileAttributes.System)) == 0);
            if (dir is not null)
            {
                AnchorPath = dir;
                Save();
                return dir;
            }
        }
        catch (Exception ex) { AppLog.Warn($"挑锚点文件夹失败：{ex.Message}"); }

        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }
}
