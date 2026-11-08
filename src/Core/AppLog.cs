using System.Text;

namespace DeskTidy.Core;

/// <summary>
/// 极简文件日志。WinExe 没有控制台，所有诊断信息落到 %LOCALAPPDATA%\DeskTidy\logs。
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static string _dir = "";
    private static string _file = "";
    public static bool EchoToConsole { get; set; }

    public static string LogDirectory => _dir;

    public static void Init()
    {
        _dir = AppPaths.LogsDir;
        try { Directory.CreateDirectory(_dir); } catch { _dir = Path.GetTempPath(); }
        _file = Path.Combine(_dir, "falsifile.log");
        // 只保留最近一次运行：超过 2MB 就轮转一次
        try
        {
            if (File.Exists(_file) && new FileInfo(_file).Length > 2 * 1024 * 1024)
                File.Move(_file, Path.Combine(_dir, "falsifile.old.log"), overwrite: true);
        }
        catch { /* 日志轮转失败不影响运行 */ }
        Write("BOOT", $"pid={Environment.ProcessId} os={Environment.OSVersion.VersionString}");
    }

    public static void Write(string tag, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}";
        lock (Gate)
        {
            try { File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8); } catch { }
            if (EchoToConsole)
            {
                try { Console.WriteLine(line); } catch { }
            }
        }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);

    public static void Exception(string where, Exception ex) =>
        Write("EXC ", $"{where}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
}
