using Vortice.Direct2D1;
using DeskTidy.Core;
using DeskTidy.Visual;

namespace DeskTidy.UI;

/// <summary>
/// 飞行精灵的动画器。
///
/// 吸入：图标从原位加速冲向玻璃徽章，接近时缩小 + 淡出，被徽章（窗口在画布之上）压住。
/// 回归：从徽章减速飞到记录的格子中心，落地即淡出，frost 遮罩随后揭开。
///
/// 帧推进由 WM_TIMER(15ms) 驱动，用真实时间差计算进度，机器慢也不会变形。
/// </summary>
internal sealed class FlyAnimator : IDisposable
{
    private sealed class Sprite
    {
        public required ID2D1Bitmap Tex;
        public float FromCx, FromCy, FromW, FromH;
        public float ToCx, ToCy, ToW, ToH;
        public float Delay, Dur;
        public bool Accelerate;
        public bool FadeIn;
    }

    private readonly CompositionHost _host;
    private readonly List<Sprite> _sprites = new();
    private readonly List<ID2D1Bitmap> _owned = new();

    private ID2D1Bitmap? _frost;

    public bool Running { get; private set; }
    public float Clock { get; private set; }
    public float TotalMs { get; private set; }
    public float FrostOpacity { get; set; }

    public FlyAnimator(CompositionHost host) => _host = host;

    /// <summary>准备一张全屏模糊底图（用来做回归时的 frost）。只在 1/8 分辨率上模糊，GPU 负责放大。</summary>
    public void PrepareFrost(Bgra wallpaper, int workW, int workH)
    {
        try
        {
            int sw = Math.Max(24, workW / 8), sh = Math.Max(24, workH / 8);
            var small = ImgOps.Resize(wallpaper, sw, sh);
            var buf = new float[sw * sh * 4];
            for (int i = 0; i < sw * sh; i++)
            {
                buf[i * 4] = small.Px[i * 4] / 255f;
                buf[i * 4 + 1] = small.Px[i * 4 + 1] / 255f;
                buf[i * 4 + 2] = small.Px[i * 4 + 2] / 255f;
                buf[i * 4 + 3] = 1f;
            }
            ImgOps.Blur(buf, sw, sh, Math.Max(2, sw / 40));

            var img = new Bgra(sw, sh);
            for (int i = 0; i < sw * sh; i++)
            {
                img.Px[i * 4] = (byte)Math.Clamp((int)(buf[i * 4] * 255), 0, 255);
                img.Px[i * 4 + 1] = (byte)Math.Clamp((int)(buf[i * 4 + 1] * 255), 0, 255);
                img.Px[i * 4 + 2] = (byte)Math.Clamp((int)(buf[i * 4 + 2] * 255), 0, 255);
                img.Px[i * 4 + 3] = 255;
            }

            _frost?.Dispose();
            _frost = _host.CreateTexture(img);
            AppLog.Info($"frost 贴图就绪 {sw}x{sh}");
        }
        catch (Exception ex) { AppLog.Exception("PrepareFrost", ex); }
    }

    public void Begin()
    {
        ClearSprites();
        Clock = 0;
        TotalMs = 0;
        Running = true;
    }

    /// <summary>加一个精灵。坐标是画布局部坐标（= 屏幕 - 工作区原点）。</summary>
    public bool AddSprite(Bgra icon, float fromCx, float fromCy, float fromW, float fromH,
        float toCx, float toCy, float toW, float toH, float delayMs, float durMs,
        bool accelerate, bool fadeIn)
    {
        var tex = _host.CreateTexture(icon);
        if (tex is null) return false;
        _owned.Add(tex);
        _sprites.Add(new Sprite
        {
            Tex = tex,
            FromCx = fromCx, FromCy = fromCy, FromW = fromW, FromH = fromH,
            ToCx = toCx, ToCy = toCy, ToW = toW, ToH = toH,
            Delay = delayMs, Dur = Math.Max(60, durMs), Accelerate = accelerate, FadeIn = fadeIn,
        });
        TotalMs = Math.Max(TotalMs, delayMs + durMs);
        return true;
    }

    /// <summary>推进一帧并画出来。返回 true 表示动画结束。</summary>
    public bool Step(float dtMs)
    {
        if (!Running) return true;
        Clock += dtMs;

        _host.BeginFrame();

        if (_frost is not null && FrostOpacity > 0.002f)
        {
            var wa = _host.Width;
            var ha = _host.Height;
            _host.DrawTexture(_frost, 0, 0, wa, ha, FrostOpacity);
        }

        foreach (var s in _sprites)
        {
            float t = (Clock - s.Delay) / s.Dur;
            if (t < 0) t = 0;          // 还没到出发时间：就停在起点（盖住原图标），不要跳过
            t = Math.Min(1f, t);

            float p = s.Accelerate
                ? MathF.Pow(t, 1.85f)                       // 吸入：起步慢，末段猛
                : 1f - MathF.Pow(1f - t, 2.4f);             // 回归：起步快，末段黏住

            float cx = s.FromCx + (s.ToCx - s.FromCx) * p;
            float cy = s.FromCy + (s.ToCy - s.FromCy) * p;
            float w = s.FromW + (s.ToW - s.FromW) * p;
            float h = s.FromH + (s.ToH - s.FromH) * p;

            float opacity;
            if (s.FadeIn)
                opacity = Math.Clamp(t / 0.18f, 0f, 1f) * Math.Clamp((1f - t) / 0.25f + 0.15f, 0f, 1f);
            else
                opacity = Math.Clamp((1f - t) / 0.22f, 0f, 1f);

            _host.DrawTexture(s.Tex, cx - w / 2, cy - h / 2, w, h, opacity);
        }

        _host.EndFrame();

        return Clock >= TotalMs + 40;
    }

    public void Stop()
    {
        Running = false;
        _host.BeginFrame();
        _host.EndFrame();
    }

    public void ClearSprites()
    {
        foreach (var t in _owned) { try { t.Dispose(); } catch { } }
        _owned.Clear();
        _sprites.Clear();
        TotalMs = 0;
    }

    public void Dispose()
    {
        ClearSprites();
        _frost?.Dispose();
        _frost = null;
    }
}
