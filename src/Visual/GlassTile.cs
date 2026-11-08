namespace DeskTidy.Visual;

/// <summary>
/// 毛玻璃贴图（CPU 合成，一次性生成的静态图层）。
///
/// 视觉构成：
///   背景采样 → 霜化模糊 → 边缘折射（透镜）→ 壁纸染色 → 内阴影 → 顶边高光 → 外缘反光
///   → 颗粒 → 系统图标（带自身投影）→ 双层柔和投影
///
/// 关键取舍：毛玻璃会吸一点光，所以整体比背景略暗（bright=0.97）。
/// 之前提亮到 1.02 时立刻变成"白色塑料"。
/// </summary>
internal sealed class GlassTile
{
    public Bgra Sprite { get; private init; } = null!;   // 预乘 alpha
    public int Pad { get; private init; }                 // 精灵四周留的投影空间
    public int TilePx { get; private init; }

    public static GlassTile Render(Bgra backdrop, int cx, int cy, float dpiScale, Bgra? icon,
        float tileDip = 62f, float glyphRatio = 0.74f, float shadowScale = 1f)
    {
        float s = Math.Clamp(dpiScale, 0.75f, 4f);

        int tile = Math.Max(16, (int)Math.Round(tileDip * s));

        // 所有几何量都按"瓷砖边长"的比例算，而不是按 DPI 算。
        // 这样同一套材质在 48px 的小图标和 300px 的预览里观感一致。
        int radius = Math.Max(3, (int)Math.Round(tile * 0.306f));
        int pad = Math.Max(6, (int)Math.Round(tile * 0.42f));
        int iconPx = Math.Max(8, (int)Math.Round(tile * glyphRatio));

        int S = tile + pad * 2;
        float half = tile / 2f;

        // ---- 1. 背景采样 ----
        int margin = Math.Max(6, (int)Math.Round(tile * 0.32f));
        int cw = tile + margin * 2;
        var crop = new float[cw * cw * 4];
        var avg = SampleCropToFloat(backdrop, cx - cw / 2, cy - cw / 2, cw, crop);

        // ---- 2. 霜化：半径小一点，保留壁纸结构，别糊成渐变色块 ----
        ImgOps.Blur(crop, cw, cw, Math.Max(2, (int)Math.Round(tile * 0.073f)));

        // ---- 3. 材质参数 ----
        const float tintAmount = 0.07f;   // 往这片壁纸的平均色靠拢（"跟壁纸走"）
        const float bright = 0.97f;       // 吸光
        const float sat = 1.10f;
        const float lift = 0.0f;

        var acc = new float[S * S * 4];   // 预乘 RGBA

        for (int py = 0; py < S; py++)
        {
            for (int px = 0; px < S; px++)
            {
                float tx = px - pad, ty = py - pad;
                float sd = RoundRectSdf(tx - half, ty - half, half, half, radius);
                float cover = ImgOps.Clamp01(0.5f - sd);
                int o = (py * S + px) * 4;

                // ---- 投影：近处接触阴影 + 远处柔和扩散，都很淡 ----
                float nearSd = RoundRectSdf(tx - half, ty - half - tile * 0.04f, half, half, radius);
                float farSd = RoundRectSdf(tx - half, ty - half - tile * 0.16f, half, half, radius);
                float shadowA = (0.10f * ImgOps.SmoothStep(tile * 0.16f, -2f, nearSd)
                              + 0.11f * ImgOps.SmoothStep(tile * 0.68f, -2f, farSd)) * shadowScale;
                shadowA *= 1f - cover;
                acc[o] = 0; acc[o + 1] = 0; acc[o + 2] = 0; acc[o + 3] = shadowA;

                if (cover <= 0.001f) continue;

                // ---- 边缘折射：越靠边，采样点越往中心弯 ----
                float inside = -sd;
                float edgeBand = Math.Max(1f, 0.26f * tile);
                float bend = 1f - ImgOps.SmoothStep(0f, edgeBand, inside);
                float k = tile * 0.121f * bend * bend;
                float dxc = half - tx, dyc = half - ty;
                float len = MathF.Sqrt(dxc * dxc + dyc * dyc);
                if (len > 0.001f) { dxc /= len; dyc /= len; }
                float sx = tx + dxc * k + margin;
                float sy = ty + dyc * k + margin;

                SampleBilinear(crop, cw, cw, sx, sy, out float r, out float g, out float b);

                // ---- 亮度 / 饱和 / 染色 ----
                float luma = 0.299f * r + 0.587f * g + 0.114f * b;
                r = luma + (r - luma) * sat;
                g = luma + (g - luma) * sat;
                b = luma + (b - luma) * sat;
                r = r * bright + lift;
                g = g * bright + lift;
                b = b * bright + lift;
                r += (avg.R - r) * tintAmount;
                g += (avg.G - g) * tintAmount;
                b += (avg.B - b) * tintAmount;

                // 顶部略亮、底部略暗：一块有厚度的玻璃
                float vgrad = (ty / tile - 0.5f) * 0.06f;
                r += vgrad; g += vgrad; b += vgrad;

                r = ImgOps.Clamp01(r); g = ImgOps.Clamp01(g); b = ImgOps.Clamp01(b);

                // ---- 内阴影：薄薄一圈，底部更重 ----
                float ring = MathF.Exp(-inside / Math.Max(1f, 0.030f * tile));
                float bottomWeight = 0.30f + 0.70f * (ty / tile);
                float innerShadow = 0.18f * ring * bottomWeight;

                // ---- 顶部内高光：很窄的一条亮边，玻璃的关键特征 ----
                float topGlow = 0.42f * MathF.Exp(-inside / Math.Max(0.8f, 0.011f * tile))
                                * (1f - ImgOps.SmoothStep(0f, 0.45f * tile, ty));

                // ---- 外缘镜面反光：贴着边界的一圈细亮线，左上更亮 ----
                float rimD = sd / Math.Max(0.7f, tile * 0.018f);
                float rim = 0.20f * MathF.Exp(-rimD * rimD);
                float rimSide = 1f - ImgOps.Clamp01((tx + ty) / (tile * 1.25f));
                rim *= 0.25f + 0.75f * rimSide;

                // ---- 斜向高光 ----
                float sheen = 0.05f * ImgOps.Clamp01(1f - (tx + ty) / (tile * 1.35f));

                float a = cover;
                float pr = r * cover, pg = g * cover, pb = b * cover;

                float white = sheen + topGlow + rim * cover;
                pr += white * cover;
                pg += white * cover;
                pb += white * cover;

                float darkMul = 1f - innerShadow * cover;
                pr *= darkMul; pg *= darkMul; pb *= darkMul;

                float grain = (Hash2D(px, py) - 0.5f) * 0.022f;
                pr += grain * cover; pg += grain * cover; pb += grain * cover;

                acc[o] = pr; acc[o + 1] = pg; acc[o + 2] = pb; acc[o + 3] = a;
            }
        }

        // ---- 4. 系统图标（带一层很轻的自身投影）----
        if (icon is not null)
        {
            var scaled = ImgOps.Resize(icon, iconPx, iconPx);
            int ix = pad + (tile - iconPx) / 2;
            int iy = pad + (tile - iconPx) / 2;
            DrawIconShadow(acc, S, scaled, ix, iy, Math.Max(1, (int)Math.Round(tile * 0.05f)));
            DrawIcon(acc, S, scaled, ix, iy);
        }

        // ---- 5. 输出（acc 是 RGBA 序，Bgra 缓冲是 BGRA，必须换序）----
        var sprite = new Bgra(S, S);
        for (int i = 0; i < S * S; i++)
        {
            int o = i * 4;
            sprite.Px[o] = ToByte(acc[o + 2]);
            sprite.Px[o + 1] = ToByte(acc[o + 1]);
            sprite.Px[o + 2] = ToByte(acc[o]);
            sprite.Px[o + 3] = ToByte(acc[o + 3]);
        }

        return new GlassTile { Sprite = sprite, Pad = pad, TilePx = tile };
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)Math.Round(v * 255f), 0, 255);

    private static void DrawIcon(float[] acc, int S, Bgra icon, int ix, int iy)
    {
        for (int y = 0; y < icon.H; y++)
        {
            int py = iy + y;
            if (py < 0 || py >= S) continue;
            for (int x = 0; x < icon.W; x++)
            {
                int px = ix + x;
                if (px < 0 || px >= S) continue;
                var (bb, gg, rr, aa) = icon.At(x, y);
                if (aa == 0) continue;
                float sa = aa / 255f;
                int o = (py * S + px) * 4;
                acc[o] = rr / 255f * sa + acc[o] * (1 - sa);
                acc[o + 1] = gg / 255f * sa + acc[o + 1] * (1 - sa);
                acc[o + 2] = bb / 255f * sa + acc[o + 2] * (1 - sa);
                acc[o + 3] = sa + acc[o + 3] * (1 - sa);
            }
        }
    }

    private static void DrawIconShadow(float[] acc, int S, Bgra icon, int ix, int iy, int dy)
    {
        int n = icon.W * icon.H;
        var mask = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            mask[i * 4 + 3] = icon.Px[i * 4 + 3] / 255f;
            mask[i * 4] = mask[i * 4 + 1] = mask[i * 4 + 2] = 0f;
        }
        ImgOps.Blur(mask, icon.W, icon.H, Math.Max(1, icon.W / 40));

        for (int y = 0; y < icon.H; y++)
        {
            int py = iy + y + dy;
            if (py < 0 || py >= S) continue;
            for (int x = 0; x < icon.W; x++)
            {
                int px = ix + x;
                if (px < 0 || px >= S) continue;
                float a = mask[(y * icon.W + x) * 4 + 3] * 0.30f;
                if (a <= 0.002f) continue;
                int o = (py * S + px) * 4;
                float inv = 1f - a;
                acc[o] *= inv; acc[o + 1] *= inv; acc[o + 2] *= inv;
                acc[o + 3] = a + acc[o + 3] * inv;
            }
        }
    }

    private static (float R, float G, float B) SampleCropToFloat(Bgra src, int x0, int y0, int size, float[] dst)
    {
        double r = 0, g = 0, b = 0; long n = 0;
        for (int y = 0; y < size; y++)
        {
            int sy = Math.Clamp(y0 + y, 0, src.H - 1);
            for (int x = 0; x < size; x++)
            {
                int sx = Math.Clamp(x0 + x, 0, src.W - 1);
                var (bb, gg, rr, _) = src.At(sx, sy);
                int o = (y * size + x) * 4;
                dst[o] = bb / 255f; dst[o + 1] = gg / 255f; dst[o + 2] = rr / 255f; dst[o + 3] = 1f;
                b += bb; g += gg; r += rr; n++;
            }
        }
        return n == 0 ? (0.5f, 0.5f, 0.5f) : ((float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255));
    }

    private static void SampleBilinear(float[] img, int w, int h, float x, float y,
        out float r, out float g, out float b)
    {
        x = Math.Clamp(x, 0f, w - 1.001f);
        y = Math.Clamp(y, 0f, h - 1.001f);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = x - x0, ty = y - y0;
        int i00 = (y0 * w + x0) * 4, i10 = (y0 * w + x1) * 4;
        int i01 = (y1 * w + x0) * 4, i11 = (y1 * w + x1) * 4;
        b = (img[i00] * (1 - tx) + img[i10] * tx) * (1 - ty) + (img[i01] * (1 - tx) + img[i11] * tx) * ty;
        g = (img[i00 + 1] * (1 - tx) + img[i10 + 1] * tx) * (1 - ty) + (img[i01 + 1] * (1 - tx) + img[i11 + 1] * tx) * ty;
        r = (img[i00 + 2] * (1 - tx) + img[i10 + 2] * tx) * (1 - ty) + (img[i01 + 2] * (1 - tx) + img[i11 + 2] * tx) * ty;
    }

    /// <summary>圆角矩形有符号距离场（负值在内部）。</summary>
    private static float RoundRectSdf(float px, float py, float hx, float hy, float r)
    {
        float qx = MathF.Abs(px) - (hx - r);
        float qy = MathF.Abs(py) - (hy - r);
        float ax = MathF.Max(qx, 0), ay = MathF.Max(qy, 0);
        return MathF.Sqrt(ax * ax + ay * ay) + MathF.Min(MathF.Max(qx, qy), 0) - r;
    }

    private static float Hash2D(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
    }
}
