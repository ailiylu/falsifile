using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using DeskTidy.Core;

namespace DeskTidy.UI;

/// <summary>
/// 透明画布的 GPU 侧：D3D11 + D2D1 + DirectComposition。
///
/// 为什么不用 WS_EX_LAYERED + UpdateLayeredWindow：
/// 那条路每帧要把整张 4K 位图从 CPU 拷进 DWM（~33MB/帧），正是"桌面卡顿"的来源。
/// 这里用 CreateSwapChainForComposition + DComp 视觉树，透明度由 DWM 合成器处理，
/// 空闲时不重绘、不拷贝，动画只改视觉树的 transform。
/// </summary>
internal sealed class CompositionHost : IDisposable
{
    private ID3D11Device? _d3d;
    private ID3D11DeviceContext? _ctx;
    private IDXGIDevice? _dxgiDevice;
    private IDXGIFactory2? _factory;
    private IDXGISwapChain1? _swapChain;
    private IDCompositionDevice? _dcomp;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _rootVisual;
    private ID2D1Device? _d2d;
    private ID2D1DeviceContext? _d2dCtx;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public float DpiX { get; private set; } = 96f;
    public float DpiY { get; private set; } = 96f;
    public bool IsReady => _swapChain is not null && _d2dCtx is not null;

    public ID2D1DeviceContext? Context => _d2dCtx;

    public void Initialize(IntPtr hwnd, int widthPx, int heightPx, float dpiX, float dpiY)
    {
        Width = Math.Max(1, widthPx);
        Height = Math.Max(1, heightPx);
        DpiX = dpiX <= 0 ? 96f : dpiX;
        DpiY = dpiY <= 0 ? 96f : dpiY;

        // 1) D3D11 设备：硬件优先，失败退 WARP（远程桌面/虚拟机也能跑）
        try
        {
            _d3d = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"D3D11 硬件设备创建失败，退回 WARP：{ex.Message}");
            _d3d = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport);
        }

        _ctx = _d3d.ImmediateContext;
        _dxgiDevice = _d3d.QueryInterface<IDXGIDevice>();
        using (var adapter = _dxgiDevice.GetAdapter())
            _factory = adapter.GetParent<IDXGIFactory2>();

        // 2) 合成用交换链：必须 FlipSequential + 预乘 alpha，否则 DWM 不认透明
        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None,
        };
        _swapChain = _factory.CreateSwapChainForComposition(_dxgiDevice, desc, null);

        // 3) DirectComposition 视觉树，挂在我们的 HWND 上（topmost 参数恒为 false）
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
        _dcomp.CreateTargetForHwnd(hwnd, false, out _target);
        _rootVisual = _dcomp.CreateVisual();
        _rootVisual.SetContent(_swapChain);
        _target.SetRoot(_rootVisual);
        _dcomp.Commit();

        // 4) D2D 设备上下文，用于往交换链上画
        _d2d = D2D1.D2D1CreateDevice(_dxgiDevice, null);
        _d2dCtx = _d2d.CreateDeviceContext(DeviceContextOptions.None);
        // 关键：D2D 单位固定为 96dpi，即 1 单位 == 1 物理像素。
        // 这样后面所有坐标换算都不必再乘 DPI，根除"坐标乘两遍"的坑。
        _d2dCtx.SetDpi(96f, 96f);

        AppLog.Info($"Composition 就绪 {Width}x{Height} @ {DpiX:0}dpi");
    }

    private bool _frameOpen;

    /// <summary>开一帧：清全透明，准备画多个精灵。</summary>
    public void BeginFrame()
    {
        if (!IsReady || _frameOpen) return;
        var backBuffer = _swapChain!.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1
        {
            PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            DpiX = 96f,
            DpiY = 96f,
            BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw,
        };
        _frameTarget = _d2dCtx!.CreateBitmapFromDxgiSurface(backBuffer, props);
        _frameBuffer = backBuffer;
        _d2dCtx.Target = _frameTarget;
        _d2dCtx.BeginDraw();
        _d2dCtx.Clear(new Color4(0f, 0f, 0f, 0f));
        _frameOpen = true;
    }

    private ID2D1Bitmap1? _frameTarget;
    private IDXGISurface? _frameBuffer;

    public void EndFrame()
    {
        if (!_frameOpen) return;
        _d2dCtx!.EndDraw();
        _d2dCtx.Target = null;
        _frameTarget?.Dispose();
        _frameTarget = null;
        _frameBuffer?.Dispose();
        _frameBuffer = null;
        _frameOpen = false;
        _swapChain!.Present(1, PresentFlags.None);
    }

    /// <summary>上传一张图片为可复用的 GPU 位图（调用方负责 Dispose）。</summary>
    public ID2D1Bitmap? CreateTexture(DeskTidy.Visual.Bgra img)
    {
        if (!IsReady) return null;
        var props = new BitmapProperties1
        {
            PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            DpiX = 96f,
            DpiY = 96f,
        };
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            img.Px, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return _d2dCtx!.CreateBitmap(
                new Vortice.Mathematics.SizeI(img.W, img.H),
                handle.AddrOfPinnedObject(),
                (uint)(img.W * 4),
                props);
        }
        finally { handle.Free(); }
    }

    /// <summary>在 BeginFrame/EndFrame 之间画一张纹理到指定矩形。</summary>
    public void DrawTexture(ID2D1Bitmap bmp, float x, float y, float w, float h, float opacity)
    {
        if (!_frameOpen || opacity <= 0.002f) return;
        var t = System.Numerics.Matrix3x2.CreateScale(
                    w / bmp.Size.Width, h / bmp.Size.Height)
                * System.Numerics.Matrix3x2.CreateTranslation(x, y);
        _d2dCtx!.Transform = t;
        _d2dCtx.DrawBitmap(bmp, Math.Clamp(opacity, 0f, 1f),
            Vortice.Direct2D1.BitmapInterpolationMode.Linear);
        _d2dCtx.Transform = System.Numerics.Matrix3x2.Identity;
    }

    /// <summary>把整个画布清成全透明（0,0,0,0）。alpha=0 的像素对鼠标也是穿透的。</summary>
    public void ClearTransparentAndPresent()
    {
        RenderAndPresent(ctx => ctx.Clear(new Color4(0f, 0f, 0f, 0f)));
    }

    /// <summary>支持自定义绘制，供玻璃贴图/测试图使用。</summary>
    public void RenderAndPresent(Action<ID2D1DeviceContext> draw)    {
        if (_swapChain is null || _d2dCtx is null) return;

        using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1
        {
            PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            DpiX = 96f,
            DpiY = 96f,
            BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw,
        };
        using var targetBmp = _d2dCtx.CreateBitmapFromDxgiSurface(backBuffer, props);

        _d2dCtx.Target = targetBmp;
        _d2dCtx.BeginDraw();
        try
        {
            _d2dCtx.Clear(new Color4(0f, 0f, 0f, 0f));
            draw(_d2dCtx);
        }
        finally
        {
            _d2dCtx.EndDraw();
            _d2dCtx.Target = null;
        }

        _swapChain.Present(1, PresentFlags.None);
    }

    /// <summary>
    /// 画一个纯色矩形并呈现。用于像素级验证："图案出现在正确物理坐标" + "其余区域像素零变化"。
    /// </summary>
    public void RenderPattern(float x, float y, float w, float h, Color4 color)
    {
        RenderAndPresent(ctx =>
        {
            using var brush = ctx.CreateSolidColorBrush(color);
            ctx.FillRectangle(new Vortice.RawRectF(x, y, x + w, y + h), brush);
        });
    }

    /// <summary>把一张预乘 alpha 的位图画到指定位置（用于玻璃贴图）。</summary>
    public void RenderBitmap(DeskTidy.Visual.Bgra sprite, int x, int y) => RenderBitmapClipped(sprite, x, y, 0);

    /// <summary>画玻璃贴图；inset>0 时以图心为中心轻微收缩（按下反馈）。</summary>
    public void RenderBitmapClipped(DeskTidy.Visual.Bgra sprite, int x, int y, int inset)
    {
        if (!IsReady) return;
        RenderAndPresent(ctx =>
        {
            var props = new BitmapProperties1
            {
                PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                DpiX = 96f,
                DpiY = 96f,
            };

            var handle = System.Runtime.InteropServices.GCHandle.Alloc(
                sprite.Px, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                using var bmp = ctx.CreateBitmap(
                    new Vortice.Mathematics.SizeI(sprite.W, sprite.H),
                    handle.AddrOfPinnedObject(),
                    (uint)(sprite.W * 4),
                    props);

                // 用变换来定位/缩放，避免依赖 Rect 的构造函数语义
                var center = new System.Numerics.Vector2(sprite.W / 2f, sprite.H / 2f);
                float scale = 1f - inset / (float)sprite.W;
                var m = System.Numerics.Matrix3x2.CreateScale(scale, center)
                      * System.Numerics.Matrix3x2.CreateTranslation(x, y);

                ctx.Transform = m;
                ctx.DrawBitmap(bmp, 1f, Vortice.Direct2D1.BitmapInterpolationMode.Linear);
                ctx.Transform = System.Numerics.Matrix3x2.Identity;
            }
            finally { handle.Free(); }
        });
    }

    public void Commit() => _dcomp?.Commit();

    public void Dispose()
    {
        try { _d2dCtx?.Dispose(); } catch { }
        try { _d2d?.Dispose(); } catch { }
        try { _target?.Dispose(); } catch { }
        try { _rootVisual?.Dispose(); } catch { }
        try { _swapChain?.Dispose(); } catch { }
        try { _dcomp?.Dispose(); } catch { }
        try { _factory?.Dispose(); } catch { }
        try { _dxgiDevice?.Dispose(); } catch { }
        try { _ctx?.Dispose(); } catch { }
        try { _d3d?.Dispose(); } catch { }
    }
}
