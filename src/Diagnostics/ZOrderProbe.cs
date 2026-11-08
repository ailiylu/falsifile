using System.Text;
using DeskTidy.Interop;
using static DeskTidy.Interop.NativeMethods;

namespace DeskTidy.Diagnostics;

internal sealed record ZEntry(
    int Index, IntPtr Hwnd, string ClassName, string Title, uint Pid, bool Visible, bool TopMost, bool IsOurs);

/// <summary>枚举真实 Z 序，用来证明"我们永远在普通窗口之下、桌面之上"。</summary>
internal static class ZOrderProbe
{
    public static List<ZEntry> Snapshot(IntPtr ours)
    {
        var list = new List<ZEntry>();
        var h = GetTopWindow(IntPtr.Zero);
        int i = 0;
        while (h != IntPtr.Zero && i < 4096)
        {
            GetWindowThreadProcessId(h, out var pid);
            int ex = ExStyleOf(h);
            list.Add(new ZEntry(
                i++, h,
                ClassNameOf(h),
                TitleOf(h) is { Length: > 60 } t ? t[..60] : TitleOf(h),
                pid,
                IsWindowVisible(h),
                (ex & WS_EX_TOPMOST) != 0,
                h == ours));
            h = GetWindow(h, GW_HWNDNEXT);
        }
        return list;
    }

    public static string Dump(List<ZEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.AppendLine($"  #{e.Index,3} 0x{e.Hwnd.ToInt64():X8} {(e.Visible ? "V" : "-")} {(e.TopMost ? "T" : "-")} {e.ClassName,-40} pid={e.Pid,-6} {e.Title}");
        }
        return sb.ToString();
    }

    /// <summary>桌面窗口（Progman / 承载图标的 WorkerW）。</summary>
    public static IntPtr FindDesktopWindow()
    {
        var progman = FindWindowW("Progman", null);
        return progman != IntPtr.Zero ? progman : FindWindowW("WorkerW", null);
    }
}
