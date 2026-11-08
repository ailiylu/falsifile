using System.Diagnostics;
using DeskTidy.Core;
using DeskTidy.Interop;
using static DeskTidy.Interop.NativeMethods;

namespace DeskTidy.Core;

/// <summary>
/// 看门狗。每 250ms 校验一次"绝不出事"的几条硬约束，
/// 一旦发现窗口被置顶、抢了焦点、越界，立刻修复；修不好就自杀式退出。
/// 这是防止"桌面被盖住、只能重启"的最后一道闸。
/// </summary>
internal sealed class Guardian : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Func<RECT> _getWorkArea;
    private System.Threading.Timer? _timer;
    private int _unrepairedFailures;

    public int Violations { get; private set; }
    public Guardian(IntPtr hwnd, Func<RECT> getWorkArea)
    {
        _hwnd = hwnd;
        _getWorkArea = getWorkArea;
    }

    public void Start()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, 250, 250);
    }

    private void Tick()
    {
        try
        {
            if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

            bool repaired = true;

            // 1) 绝不允许 TOPMOST
            int ex = ExStyleOf(_hwnd);
            if ((ex & WS_EX_TOPMOST) != 0)
            {
                Violations++;
                AppLog.Warn("看门狗：发现 WS_EX_TOPMOST，正在剥离");
                SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_TOPMOST));
                SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                repaired = false;
            }

            // 2) 绝不允许成为前台窗口（抢焦点）
            var fg = GetForegroundWindow();
            if (fg == _hwnd)
            {
                Violations++;
                AppLog.Warn("看门狗：画布成了前台窗口，正在压回桌面层");
                SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                repaired = false;
            }

            // 3) 尺寸必须严格等于工作区（不越界、不盖任务栏）
            if (GetWindowRect(_hwnd, out var r))
            {
                var wa = _getWorkArea();
                bool samePos = r.Left == wa.Left && r.Top == wa.Top;
                bool sameSize = r.Width == wa.Width && r.Height == wa.Height;
                if (!samePos || !sameSize)
                {
                    Violations++;
                    AppLog.Warn($"看门狗：窗口越界 {r.Left},{r.Top},{r.Width}x{r.Height} -> {wa.Left},{wa.Top},{wa.Width}x{wa.Height}");
                    SetWindowPos(_hwnd, IntPtr.Zero, wa.Left, wa.Top, wa.Width, wa.Height,
                        SWP_NOZORDER | SWP_NOACTIVATE);
                    repaired = false;
                }
            }

            if (!repaired)
            {
                _unrepairedFailures++;
                if (_unrepairedFailures >= 5)
                {
                    AppLog.Error("看门狗：连续 5 次修复失败，进程退出以防桌面被占");
                    Environment.Exit(97);
                }
            }
            else
            {
                _unrepairedFailures = 0;
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("Guardian.Tick", ex);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
