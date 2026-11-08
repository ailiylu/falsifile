using System.Diagnostics;
using DeskTidy.Core;

namespace DeskTidy.Core;

/// <summary>
/// 单实例。第二个实例启动时：先礼（通知旧实例退出）后兵（进程被 kill）。
/// 完全按需求"启动时检测已有实例，先杀掉旧实例再启动"，只是先给 2 秒体面退出的机会。
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\DeskTidy.SingleInstance.v1";
    private const string ExitEventName = @"Local\DeskTidy.ExitRequest.v1";

    private static Mutex? _mutex;
    private static EventWaitHandle? _exitEvent;

    public static string DataDir => AppPaths.DataDir;

    private static string PidFile => Path.Combine(DataDir, "instance.pid");

    /// <summary>拿到实例所有权。返回 false 表示抢不到（不该发生）。</summary>
    public static bool Acquire(TimeSpan gracePeriod)
    {
        Directory.CreateDirectory(DataDir);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var created = false;
            try
            {
                _mutex = new Mutex(initiallyOwned: true, MutexName, out created);
            }
            catch (AbandonedMutexException)
            {
                // 上一个实例是崩溃退出的，我们接管
                _mutex = new Mutex(initiallyOwned: true, MutexName, out created);
                created = true;
            }

            if (created)
            {
                WritePid();
                _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
                AppLog.Info($"单实例：已获得所有权 pid={Environment.ProcessId}");
                return true;
            }

            // 已有实例：请求它体面退出
            AppLog.Info("单实例：检测到已运行的实例，发退出信号");
            SignalExistingToExit();
            bool exited = WaitForPidExit(gracePeriod);
            if (!exited)
            {
                KillExisting();
                WaitForPidExit(TimeSpan.FromSeconds(1));
            }

            _mutex.Dispose();
            _mutex = null;
            Thread.Sleep(200);
        }

        AppLog.Error("单实例：3 次尝试后仍无法获得所有权");
        return false;
    }

    private static void WritePid()
    {
        try { File.WriteAllText(PidFile, Environment.ProcessId.ToString()); }
        catch (Exception ex) { AppLog.Warn($"写 pid 文件失败：{ex.Message}"); }
    }

    private static int ReadPid()
    {
        try
        {
            var s = File.ReadAllText(PidFile).Trim();
            return int.TryParse(s, out var pid) ? pid : 0;
        }
        catch { return 0; }
    }

    private static void SignalExistingToExit()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var ev))
            {
                ev.Set();
                ev.Dispose();
            }
        }
        catch (Exception ex) { AppLog.Warn($"发退出信号失败：{ex.Message}"); }
    }

    private static bool WaitForPidExit(TimeSpan timeout)
    {
        int pid = ReadPid();
        if (pid <= 0 || pid == Environment.ProcessId) return true;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return true;
            }
            catch (ArgumentException) { return true; }
            catch { return false; }
            Thread.Sleep(100);
        }
        return false;
    }

    private static void KillExisting()
    {
        int pid = ReadPid();
        if (pid <= 0 || pid == Environment.ProcessId) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            AppLog.Warn($"单实例：旧实例 {pid} 未响应，强制结束");
            p.Kill(entireProcessTree: true);
            p.WaitForExit(2000);
        }
        catch (Exception ex) { AppLog.Warn($"结束旧实例失败：{ex.Message}"); }
    }

    /// <summary>本实例被"新实例"要求退出时触发。</summary>
    public static void ListenForExitRequest(Action onExitRequested)
    {
        if (_exitEvent is null) return;
        var t = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    if (_exitEvent.WaitOne())
                    {
                        AppLog.Info("收到新实例的退出请求");
                        onExitRequested();
                        return;
                    }
                }
            }
            catch (Exception ex) { AppLog.Exception("ExitListener", ex); }
        })
        { IsBackground = true, Name = "DeskTidy.ExitListener" };
        t.Start();
    }

    public static void Release()
    {
        try { File.Delete(PidFile); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        try { _exitEvent?.Dispose(); } catch { }
    }
}
