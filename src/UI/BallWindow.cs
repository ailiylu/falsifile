using System.Runtime.InteropServices;
using DeskTidy.Core;
using DeskTidy.Interop;
using static DeskTidy.Interop.NativeMethods;

namespace DeskTidy.UI;

/// <summary>
/// 悬浮球。整个程序唯一的常驻窗口，也是唯一的入口。
///
/// 约束：
///   * 永不 TOPMOST —— 任何窗口都能盖住它，任务栏永远在它上面，不遮挡任何东西
///   * NOACTIVATE / TOOLWINDOW —— 不抢焦点、不进 Alt+Tab
///   * 窗口比球大一圈（为了画投影），但光标在球以外的区域时返回 HTTRANSPARENT，
///     点击会穿透到桌面，所以多出来的边不吞鼠标
///
/// 拖动只调 SetWindowPos，不重画内容 —— 这是不卡的关键。
/// </summary>
internal sealed class BallWindow : IDisposable
{
    private const string ClassName = "DeskTidy.Ball";
    private static WndProcDelegate? _wndProc;
    private static BallWindow? _instance;

    private IntPtr _hwnd;
    private ushort _atom;

    private bool _dragging;
    private bool _moved;
    private DeskTidy.Visual.Bgra? _lastSprite;
    private int _dragStartX, _dragStartY;
    private Interop.NativeMethods.POINT _dragStartCursor;

    public IntPtr Handle => _hwnd;
    public CompositionHost Host { get; } = new();
    public int Size { get; private set; }          // 窗口边长（含投影留白）
    public int TilePx { get; private set; }        // 球本体边长
    public int Pad { get; private set; }

    public event Action? Clicked;
    public event Action? DoubleClicked;
    public event Action? RightClicked;
    public event Action<int, int>? Dragged;        // 松手后的窗口左上角

    public static BallWindow Create(int spriteSize, int tilePx, int pad)
    {
        var w = new BallWindow
        {
            Size = Math.Max(32, spriteSize),
            TilePx = Math.Max(16, tilePx),
            Pad = Math.Max(0, pad),
        };
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
            throw new InvalidOperationException($"RegisterClassExW(ball) 失败 err={Marshal.GetLastWin32Error()}");

        _hwnd = CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName, "", WS_POPUP, 0, 0, Size, Size,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW(ball) 失败 err={Marshal.GetLastWin32Error()}");

        Host.Initialize(_hwnd, Size, Size, 96f, 96f);
        AppLog.Info($"悬浮球创建 hwnd=0x{_hwnd:X} window={Size} tile={TilePx} pad={Pad}");
    }

    public void ShowAt(int left, int top)
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, IntPtr.Zero, left, top, Size, Size, SWP_NOZORDER | SWP_NOACTIVATE);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    /// <summary>把球压到桌面层（只在启动时调一次；调用时球还没显示，不会闪）。</summary>
    public void PlaceAtDesktopBand()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 保证球在画布之上。只把画布压下去，绝不动球 ——
    /// 以前每次都先把球 SetWindowPos 到 HWND_BOTTOM，那一瞬球落到壁纸下面，看起来就是闪一下。
    /// </summary>
    public void PlaceAbove(IntPtr canvas)
    {
        if (_hwnd == IntPtr.Zero || canvas == IntPtr.Zero) return;
        SetWindowPos(canvas, _hwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void SetSprite(DeskTidy.Visual.Bgra sprite)
    {
        _lastSprite = sprite;
        Host.RenderBitmap(sprite, 0, 0);
    }

    /// <summary>"没有可清理的东西"时给一个收缩回弹的反馈——不然点了像没反应。</summary>
    public void Pulse()
    {
        if (_hwnd == IntPtr.Zero || _lastSprite is null) return;
        Host.RenderBitmapClipped(_lastSprite, 0, 0, Math.Max(3, TilePx / 18));
        SetTimer(_hwnd, 3, 150, IntPtr.Zero);
    }

    public (int Left, int Top) Position
    {
        get
        {
            GetWindowRect(_hwnd, out var r);
            return (r.Left, r.Top);
        }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return new IntPtr(1);

            case WM_NCHITTEST:
            {
                // 只有球本体接受鼠标，投影留白区域穿透到桌面
                int sx = (short)(lParam.ToInt64() & 0xFFFF);
                int sy = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                var p = new Interop.NativeMethods.POINT { X = sx, Y = sy };
                ScreenToClient(hWnd, ref p);
                return _instance is not null && _instance.HitBall(p)
                    ? new IntPtr(1) /* HTCLIENT */
                    : HTTRANSPARENT;
            }

            case 0x0201: // WM_LBUTTONDOWN
                _instance?.OnDown();
                return IntPtr.Zero;

            case 0x0202: // WM_LBUTTONUP
                _instance?.OnUp();
                return IntPtr.Zero;

            case 0x0203: // WM_LBUTTONDBLCLK
                _instance?.DoubleClicked?.Invoke();
                return IntPtr.Zero;

            case 0x0205: // WM_RBUTTONUP
                _instance?.RightClicked?.Invoke();
                return IntPtr.Zero;

            case 0x0200: // WM_MOUSEMOVE
                _instance?.OnMove();
                return IntPtr.Zero;

            case 0x0020: // WM_SETCURSOR
                return new IntPtr(1);

            case WM_TIMER:
                if (wParam == new IntPtr(3) && _instance is not null)
                {
                    KillTimer(hWnd, 3);
                    if (_instance._lastSprite is not null)
                        _instance.Host.RenderBitmap(_instance._lastSprite, 0, 0);
                }
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private bool HitBall(Interop.NativeMethods.POINT client)
    {
        // 圆形命中，略放宽 2px 让边缘好点
        float cx = Pad + TilePx / 2f;
        float cy = Pad + TilePx / 2f;
        float r = TilePx / 2f + 2;
        float dx = client.X - cx, dy = client.Y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    private void OnDown()
    {
        GetCursorPos(out _dragStartCursor);
        var (l, t) = Position;
        _dragStartX = l;
        _dragStartY = t;
        _moved = false;
        _dragging = true;
        SetCapture(_hwnd);
    }

    private void OnMove()
    {
        if (!_dragging) return;
        if (!GetCursorPos(out var cur)) return;

        int dx = cur.X - _dragStartCursor.X;
        int dy = cur.Y - _dragStartCursor.Y;
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) > 3) _moved = true;
        if (!_moved) return;

        // 只移动窗口，不重画 —— 拖动期间零重绘
        SetWindowPos(_hwnd, IntPtr.Zero, _dragStartX + dx, _dragStartY + dy, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void OnUp()
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseCapture();
        }

        if (_moved)
        {
            var (l, t) = Position;
            Dragged?.Invoke(l, t);
        }
        else
        {
            Clicked?.Invoke();
        }
    }

    public void Dispose()
    {
        Host.Dispose();
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        _instance = null;
    }
}
