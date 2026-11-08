using DeskTidy.Core;

namespace DeskTidy.Core;

/// <summary>
/// 数据目录。优先 %LOCALAPPDATA%\DeskTidy；被 ACL 拒绝（某些受限环境）时依次退到
/// exe 同级、临时目录。任何情况下都不能因为"写不了日志"直接崩溃。
/// </summary>
internal static class AppPaths
{
    public static string DataDir { get; } = Resolve();

    public static string LogsDir { get; } = Path.Combine(DataDir, "logs");
    public static string JournalPath => Path.Combine(DataDir, "journal.json");

    private static string Resolve()
    {
        // 便携/调试模式：环境变量优先，方便把日志与状态放到指定目录
        var overrideDir = Environment.GetEnvironmentVariable("DESKTIDY_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            try
            {
                Directory.CreateDirectory(overrideDir);
                Directory.CreateDirectory(Path.Combine(overrideDir, "logs"));
                return overrideDir;
            }
            catch { /* 退到默认路径 */ }
        }

        var candidates = new List<string>
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "falsifile"),
            Path.Combine(AppContext.BaseDirectory, "falsifileData"),
            Path.Combine(Path.GetTempPath(), "falsifile"),
        };

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(Path.Combine(dir, "logs"));
                return dir;
            }
            catch
            {
                // 试下一个
            }
        }
        return Path.GetTempPath();
    }

    public static void BestEffort(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) { AppLog.Warn($"{what} 失败：{ex.Message}"); }
    }
}
