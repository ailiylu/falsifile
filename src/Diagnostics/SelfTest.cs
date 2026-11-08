using System.Text;
using DeskTidy.Core;
using DeskTidy.Interop;
using DeskTidy.UI;
using static DeskTidy.Interop.NativeMethods;

namespace DeskTidy.Diagnostics;

/// <summary>
/// M0 自检：证明画布窗口确实"透明、穿透、不置顶、不抢焦点、不进 Alt+Tab、不盖任务栏"。
/// 输出同时写日志文件和 stdout，退出码 0=全通过。
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var sb = new StringBuilder();
        int failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  |  " + detail : "")}");
            AppLog.Info($"selftest {(ok ? "PASS" : "FAIL")} {name} {detail}");
        }

        OverlayWindow? win = null;
        try
        {
            sb.AppendLine("=== DeskTidy M0 自检 ===");
            sb.AppendLine($"pid={Environment.ProcessId}  dpi-awareness={DescribeDpiAwareness()}");

            win = OverlayWindow.Create();

            // --- 1. 窗口样式 ---
            int ex = ExStyleOf(win.Handle);
            Check("无 WS_EX_TOPMOST", (ex & WS_EX_TOPMOST) == 0, $"exstyle=0x{ex:X8}");
            Check("有 WS_EX_NOREDIRECTIONBITMAP", (ex & WS_EX_NOREDIRECTIONBITMAP) != 0);
            Check("有 WS_EX_TRANSPARENT（鼠标穿透）", (ex & WS_EX_TRANSPARENT) != 0);
            Check("有 WS_EX_NOACTIVATE（不抢焦点）", (ex & WS_EX_NOACTIVATE) != 0);
            Check("有 WS_EX_TOOLWINDOW（不进 Alt+Tab）", (ex & WS_EX_TOOLWINDOW) != 0);
            Check("无 WS_EX_LAYERED（走 DComp，避免每帧 CPU 拷贝）", (ex & WS_EX_LAYERED) == 0);

            // --- 2. 尺寸 ---
            Check("Composition 已初始化", win.Host.IsReady);
            GetWindowRect(win.Handle, out var wr);
            var wa = win.WorkArea;
            Check("窗口矩形 == 工作区",
                wr.Left == wa.Left && wr.Top == wa.Top && wr.Width == wa.Width && wr.Height == wa.Height,
                $"win={wr.Left},{wr.Top},{wr.Width}x{wr.Height} work={wa.Left},{wa.Top},{wa.Width}x{wa.Height}");

            // --- 3. 透明帧 ---
            bool presented = true;
            try
            {
                for (int i = 0; i < 10; i++) win.Host.ClearTransparentAndPresent();
            }
            catch (Exception e) { presented = false; AppLog.Exception("present", e); }
            Check("连续 10 帧透明 Present 成功", presented);

            // --- 4. 显示后仍未抢焦点、未置顶 ---
            var fgBefore = GetForegroundWindow();
            win.Show();
            PumpFor(500);

            Check("显示后不是前台窗口", GetForegroundWindow() != win.Handle,
                $"fg=0x{GetForegroundWindow().ToInt64():X}");
            Check("显示后仍无 TOPMOST", (ExStyleOf(win.Handle) & WS_EX_TOPMOST) == 0);
            Check("显示后窗口可见", IsWindowVisible(win.Handle));
            Check("显示前后前台窗口未变", GetForegroundWindow() == fgBefore,
                $"before=0x{fgBefore.ToInt64():X} after=0x{GetForegroundWindow().ToInt64():X}");

            // --- 5. Z 序位置 ---
            var z = ZOrderProbe.Snapshot(win.Handle);
            var ours = z.FirstOrDefault(e => e.IsOurs);
            sb.AppendLine();
            sb.AppendLine($"--- Z 序快照（#0 最顶，共 {z.Count} 个窗口，只显示关键切片）---");
            int oi = ours?.Index ?? z.Count;
            var shown = z.Where(e => e.Index < 8 || Math.Abs(e.Index - oi) <= 3 || e.Index >= z.Count - 2).ToList();
            int lastPrinted = -1;
            foreach (var e in shown)
            {
                if (lastPrinted >= 0 && e.Index != lastPrinted + 1) sb.AppendLine("  ...");
                sb.AppendLine($"  #{e.Index,3} 0x{e.Hwnd.ToInt64():X8} {(e.Visible ? "V" : "-")} {e.ClassName,-38} pid={e.Pid,-6} {e.Title}");
                lastPrinted = e.Index;
            }
            sb.AppendLine();
            var desktop = ZOrderProbe.FindDesktopWindow();
            var desktopEntry = z.FirstOrDefault(e => e.Hwnd == desktop);
            var trayEntry = z.FirstOrDefault(e => e.ClassName == "Shell_TrayWnd");

            Check("在 Z 序里找到自己", ours is not null);
            if (ours is not null && desktopEntry is not null)
            {
                Check("位于桌面窗口之上（可见）", ours.Index < desktopEntry.Index,
                    $"ours=#{ours.Index} desktop({desktopEntry.ClassName})=#{desktopEntry.Index}");
            }
            else
            {
                Check("位于桌面窗口之上（可见）", false, "未找到桌面窗口");
            }

            // 所有可见的普通窗口都应该在我们上面（index 更小）
            var above = z.Where(e => e.Visible && !e.IsOurs
                                     && e.ClassName != "Progman" && e.ClassName != "WorkerW"
                                     && e.ClassName != "Shell_TrayWnd"
                                     && (ExStyleOf(e.Hwnd) & WS_EX_TOOLWINDOW) == 0)
                         .ToList();
            if (ours is not null)
            {
                var violating = above.Where(e => e.Index > ours.Index).ToList();
                Check("没有任何普通窗口被我们压在下面", violating.Count == 0,
                    violating.Count == 0 ? $"{above.Count} 个可见普通窗口全部在我们之上"
                                         : "违规: " + string.Join(", ", violating.Select(v => $"{v.ClassName}#{v.Index}")));
            }
            if (trayEntry is not null && ours is not null)
                Check("任务栏在我们之上", trayEntry.Index < ours.Index, $"tray=#{trayEntry.Index} ours=#{ours.Index}");

            // --- 6. 看门狗不报错 ---
            var guardian = new Guardian(win.Handle, () => win.WorkArea);
            guardian.Start();
            PumpFor(800);
            Check("看门狗运行 800ms 无违规", guardian.Violations == 0, $"violations={guardian.Violations}");
            guardian.Dispose();

            win.Hide();
            Check("隐藏后窗口不可见", !IsWindowVisible(win.Handle));
        }
        catch (Exception ex)
        {
            failures++;
            sb.AppendLine($"FAIL  自检过程抛异常: {ex}");
            AppLog.Exception("SelfTest", ex);
        }
        finally
        {
            try { win?.Dispose(); } catch { }
        }

        sb.AppendLine();
        sb.AppendLine(failures == 0 ? "结果: 全部通过" : $"结果: {failures} 项失败");
        sb.AppendLine($"日志: {AppLog.LogDirectory}");

        var report = sb.ToString();
        var reportPath = Path.Combine(AppLog.LogDirectory, "selftest.txt");
        try { File.WriteAllText(reportPath, report, Encoding.UTF8); } catch { }
        try { Console.Out.Write(report); Console.Out.Flush(); } catch { }

        return failures == 0 ? 0 : 1;
    }

    private static string DescribeDpiAwareness()
    {
        var ctx = GetThreadDpiAwarenessContext();
        // DPI_AWARENESS_CONTEXT_* 是 -1..-6 的伪句柄，必须用 AreDpiAwarenessContextsEqual 比较
        if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-4))) return "PerMonitorV2 (正确)";
        if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-3))) return "PerMonitorV1 (不够，需要 V2)";
        if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-5))) return "Unaware";
        if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-2))) return "SystemAware (错误)";
        return $"awareness={GetAwarenessFromDpiAwarenessContext(ctx)}";
    }

    /// <summary>抽水式消息循环，让 DWM 有机会处理 z-order/显示状态变化。</summary>
    private static void PumpFor(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            while (PeekMessage(out var m, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref m);
                DispatchMessageW(ref m);
            }
            Thread.Sleep(10);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
}
