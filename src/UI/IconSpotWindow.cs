using System.Runtime.InteropServices;
using DeskTidy.Core;
using DeskTidy.Interop;
using static DeskTidy.Interop.NativeMethods;

namespace DeskTidy.UI;

/// <summary>
/// 贴在锚点图标正上方的那个小窗。它是整个程序唯一"能被点到"的窗口。
///
/// 硬性约束（和画布一致）：
///   * 永不 TOPMOST；永不成为前台；不进 Alt+Tab（TOOLWINDOW）；不抢焦点（NOACTIVATE）
///   * 尺寸只有一个图标（46 DIP），不覆盖任何别的东西
///   * 位置永远由"读到的图标坐标"驱动，不猜
/// 它没有 WS_EX_TRANSPARENT —— 因为它必须接住点击。这也是它必须做得很小的原因：
/// 窗口矩形之外的区域属于桌面，不该被我们吃掉。
/// </summary>
internal sealed class IconSpotWindow : IDisposable
{
    private const string ClassName = "DeskTidy.IconSpot";
    private static WndProcDelegate? _wndProc;
    private static IconSpotWindow? _instance;

    private IntPtr _hwnd;
    private ushort _atom;
    private bool _pressed;

    public IntPtr Handle => _hwnd;
    public CompositionHost Host { get; } = new();
    public int SizePx { get; private set; }
    public bool IsCreated => _hwnd != IntPtr.Zero;

    public event Action? Clicked;
    public event Action? DoubleClicked;
    public event Action? RightClicked;

    public static bool TraceMessages { get; set; }
        = Environment.GetEnvironmentVariable("DESKTIDY_TRACE") == "1";

    public static IconSpotWindow Create(int sizePx)
    {
        var w = new IconSpotWindow { SizePx = Math.Max(16, sizePx) };
        w.CreateInternal();
        _instance = w;
        return w;
    }

    private void CreateInternal()
    {
        var hInstance = GetModuleHandleW(null);
        _wndProc = WndProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = CS_DBLCLKS,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName,
        };
        _atom = RegisterClassExW(ref wc);
        if (_atom == 0)
            throw new InvalidOperationException($"RegisterClassExW 失败 err={Marshal.GetLastWin32Error()}");

        int exStyle = WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        // 刻意不加 WS_EX_TRANSPARENT / WS_EX_TOPMOST

        _hwnd = CreateWindowExW(exStyle, ClassName, "", WS_POPUP,
            0, 0, SizePx, SizePx, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW(spot) 失败 err={Marshal.GetLastWin32Error()}");

        Host.Initialize(_hwnd, SizePx, SizePx, 96f, 96f);
        AppLog.Info($"图标小窗创建 hwnd=0x{_hwnd:X} size={SizePx}");
    }

    /// <summary>移动到目标矩形（屏幕物理像素）并重画。</summary>
    public void Sync(int left, int top, DeskTidy.Visual.Bgra? tileSprite, int padPx)
    {
        if (_hwnd == IntPtr.Zero || tileSprite is null) return;

        _lastSprite = tileSprite;
        _lastPad = padPx;

        GetWindowRect(_hwnd, out var cur);
        if (cur.Left != left || cur.Top != top || cur.Width != SizePx || cur.Height != SizePx)
        {
            SetWindowPos(_hwnd, IntPtr.Zero, left, top, SizePx, SizePx,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }

        Draw(tileSprite, padPx);
    }

    private void Draw(DeskTidy.Visual.Bgra sprite, int padPx)
    {
        int inset = _pressed ? Math.Max(1, SizePx / 24) : 0;
        // 精灵是 (tile+2*pad) 见方，窗口只有 tile 大小，所以把精灵偏移 -pad 画进去
        Host.RenderBitmapClipped(sprite, -padPx, -padPx, inset);
    }

    public void Show()
    {
        if (_hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    public void Hide()
    {
        if (_hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, SW_HIDE);
    }

    /// <summary>把本窗口压到画布之上、普通窗口之下。</summary>
    public void PlaceAbove(IntPtr canvas)
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (canvas != IntPtr.Zero)
        {
            // 把画布压到小窗之下，等价于把小窗抬到画布之上（都不离开桌面层）
            SetWindowPos(canvas, _hwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (TraceMessages && msg != WM_PAINT)
            AppLog.Info($"spot msg 0x{msg:X}");

        switch (msg)
        {
            case WM_ERASEBKGND:
                return new IntPtr(1);

            case 0x0201: // WM_LBUTTONDOWN
                AppLog.Info("spot: WM_LBUTTONDOWN");
                _instance?.OnDown();
                return IntPtr.Zero;

            case 0x0202: // WM_LBUTTONUP
                AppLog.Info("spot: WM_LBUTTONUP");
                _instance?.OnUp();
                return IntPtr.Zero;

            case 0x0203: // WM_LBUTTONDBLCLK
                _instance?.DoubleClicked?.Invoke();
                return IntPtr.Zero;

            case 0x0205: // WM_RBUTTONUP
                _instance?.RightClicked?.Invoke();
                return IntPtr.Zero;

            case 0x0020: // WM_SETCURSOR —— 保持箭头，别变 I 型
                return new IntPtr(1);

            case WM_DESTROY:
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnDown()
    {
        _pressed = true;
        DrawLast();
    }

    private void OnUp()
    {
        if (!_pressed) return;
        _pressed = false;
        DrawLast();
        Clicked?.Invoke();
    }

    private DeskTidy.Visual.Bgra? _lastSprite;
    private int _lastPad;

    private void DrawLast()
    {
        if (_lastSprite is not null) Draw(_lastSprite, _lastPad);
    }

    public void Remember(DeskTidy.Visual.Bgra sprite, int pad)
    {
        _lastSprite = sprite;
        _lastPad = pad;
    }

    public void Dispose()
    {
        Host.Dispose();
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        _instance = null;
    }
}
