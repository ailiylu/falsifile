using System.Runtime.InteropServices;
using DeskTidy.Core;
using DeskTidy.Interop;
using static DeskTidy.Interop.NativeMethods;

namespace DeskTidy.UI;

/// <summary>
/// 那块"贴纸"：一个无边框、全透明、不置顶、点击穿透、不抢焦点的画布窗口。
///
/// 硬性约束（每一条都在运行时被 Guardian 反复校验）：
///   * 永不 WS_EX_TOPMOST
///   * 永不成为前台窗口
///   * 永不进 Alt+Tab（TOOLWINDOW）
///   * 尺寸严格等于工作区，不盖任务栏（任务栏本身是 topmost，结构上也盖不住）
///   * 空闲时隐藏，只有动画期间才 Show —— 平时桌面上没有任何多余窗口
/// </summary>
internal sealed class OverlayWindow : IDisposable
{
    private const string ClassName = "DeskTidy.OverlayCanvas";
    private static WndProcDelegate? _wndProcKeepAlive;
    private static OverlayWindow? _instance;

    private IntPtr _hwnd;
    private bool _classRegistered;
    private ushort _classAtom;

    public IntPtr Handle => _hwnd;
    public CompositionHost Host { get; } = new();
    public RECT WorkArea { get; private set; }
    public uint Dpi { get; private set; } = 96;

    public event Action<int>? HotKeyPressed;
    public event Action? DisplayChanged;
    public event Action? Tick;
    public event Action? AnimTick;

    public RECT MonitorRect { get; private set; }

    public bool IsCreated => _hwnd != IntPtr.Zero;

    public static OverlayWindow Create()
    {
        var w = new OverlayWindow();
        w.CreateInternal();
        _instance = w;
        return w;
    }

    private void CreateInternal()
    {
        var hInstance = GetModuleHandleW(null);
        _wndProcKeepAlive = WndProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            hInstance = hInstance,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero, // 不画背景，避免闪烁
            lpszClassName = ClassName,
        };
        _classAtom = RegisterClassExW(ref wc);
        if (_classAtom == 0)
            throw new InvalidOperationException($"RegisterClassExW 失败，Win32Error={Marshal.GetLastWin32Error()}");
        _classRegistered = true;

        RefreshWorkArea();

        int exStyle = WS_EX_NOREDIRECTIONBITMAP   // DirectComposition 合成，不要重定向表面
                    | WS_EX_TOOLWINDOW            // 不进 Alt+Tab
                    | WS_EX_NOACTIVATE            // 不抢焦点
                    | WS_EX_TRANSPARENT;          // 鼠标穿透
        // 注意：这里没有 WS_EX_TOPMOST，也不会有。

        _hwnd = CreateWindowExW(
            exStyle, ClassName, "DeskTidy Overlay", WS_POPUP,
            WorkArea.Left, WorkArea.Top, WorkArea.Width, WorkArea.Height,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW 失败，Win32Error={Marshal.GetLastWin32Error()}");

        Dpi = GetDpiForWindow(_hwnd);
        if (Dpi == 0) Dpi = 96;

        Host.Initialize(_hwnd, WorkArea.Width, WorkArea.Height, Dpi, Dpi);
        Host.ClearTransparentAndPresent();
        AppLog.Info($"画布窗口创建 hwnd=0x{_hwnd:X} 工作区={WorkArea.Left},{WorkArea.Top},{WorkArea.Width}x{WorkArea.Height} dpi={Dpi}");
    }

    public void RefreshWorkArea()
    {
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        var mon = MonitorFromWindow(_hwnd != IntPtr.Zero ? _hwnd : IntPtr.Zero, MONITOR_DEFAULTTONEAREST);
        if (GetMonitorInfoW(mon, ref mi))
        {
            WorkArea = mi.rcWork;
            MonitorRect = mi.rcMonitor;
        }
        else
        {
            WorkArea = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            MonitorRect = WorkArea;
        }
    }

    public void StartTick(int ms) => SetTimer(_hwnd, 1, (uint)ms, IntPtr.Zero);
    public void StopTick() => KillTimer(_hwnd, 1);
    public void StartAnimTick() => SetTimer(_hwnd, 2, 15, IntPtr.Zero);
    public void StopAnimTick() => KillTimer(_hwnd, 2);

    public static bool TraceMessages { get; set; }
        = Environment.GetEnvironmentVariable("DESKTIDY_TRACE") == "1";

    /// <summary>显示（不激活）。动画结束必须调 Hide()。</summary>
    /// <remarks>
    /// 顺序很重要：必须"先在隐藏状态下把画面清成全透明，再显示"。
    /// 反过来会先露出上一帧的内容（比如上次的 frost），看起来就是闪一下。
    /// </remarks>
    public void Show()
    {
        if (_hwnd == IntPtr.Zero) return;
        Host.ClearTransparentAndPresent();
        SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    public void Hide()
    {
        if (_hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, SW_HIDE);
    }

    /// <summary>
    /// 把窗口压到 Z 序最底：位于桌面之上、所有普通窗口之下。
    /// 这意味着任何一个普通窗口（哪怕记事本）都能盖住我们，任务栏更是永远在我们上面。
    /// </summary>
    public void PlaceAtDesktopBand()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void MoveToWorkArea()
    {
        if (_hwnd == IntPtr.Zero) return;
        RefreshWorkArea();
        SetWindowPos(_hwnd, IntPtr.Zero, WorkArea.Left, WorkArea.Top, WorkArea.Width, WorkArea.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public bool RegisterHotKeys()
    {
        bool ok1 = RegisterHotKey(_hwnd, 9001, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x51); // Ctrl+Alt+Q
        // Ctrl+Alt+R 在很多机器上被占用，退一档到 Ctrl+Alt+Shift+R，再退到 Ctrl+Alt+F12
        bool ok2 = RegisterHotKey(_hwnd, 9002, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, 0x52);
        if (!ok2) ok2 = RegisterHotKey(_hwnd, 9002, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x7B);
        AppLog.Info($"热键 退出(Ctrl+Alt+Q)={ok1} 紧急还原(Ctrl+Alt+Shift+R)={ok2}");
        return ok1;
    }

    public void UnregisterHotKeys()
    {
        UnregisterHotKey(_hwnd, 9001);
        UnregisterHotKey(_hwnd, 9002);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (TraceMessages && msg != WM_PAINT)
            AppLog.Info($"canvas msg 0x{msg:X} wp=0x{wParam:X}");

        switch (msg)
        {
            case WM_NCHITTEST:
                // 鼠标穿透：命中测试直接告诉系统"这里没有窗口"
                return HTTRANSPARENT;

            case WM_ERASEBKGND:
                return new IntPtr(1); // 背景由 DComp 提供，禁止 GDI 擦除

            case WM_PAINT:
                ValidateRect(hWnd, IntPtr.Zero);
                return IntPtr.Zero;

            case WM_HOTKEY:
                _instance?.HotKeyPressed?.Invoke(wParam.ToInt32());
                return IntPtr.Zero;

            case WM_TIMER:
                if (wParam == new IntPtr(2)) _instance?.AnimTick?.Invoke();
                else _instance?.Tick?.Invoke();
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
            case WM_SETTINGCHANGE:
                _instance?.DisplayChanged?.Invoke();
                return IntPtr.Zero;

            case WM_CLOSE:
                // 必须先隐藏再销毁：任何情况下都不能让画布留在屏幕上
                _instance?.Hide();
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        UnregisterHotKeys();
        Host.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_classRegistered && _classAtom != 0)
        {
            // 类随进程结束回收，这里只标记
            _classRegistered = false;
        }
        _instance = null;
    }
}
