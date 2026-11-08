namespace DeskTidy.Visual;

/// <summary>简单的 BGRA 位图（直通 alpha，非预乘）。</summary>
internal sealed class Bgra
{
    public int W { get; }
    public int H { get; }
    public byte[] Px { get; }

    public Bgra(int w, int h)
    {
        W = Math.Max(1, w);
        H = Math.Max(1, h);
        Px = new byte[W * H * 4];
    }

    public Bgra(int w, int h, byte[] px)
    {
        W = w; H = h; Px = px;
    }

    public Bgra Clone() => new(W, H, (byte[])Px.Clone());

    public (byte B, byte G, byte R, byte A) At(int x, int y)
    {
        int i = (y * W + x) * 4;
        return (Px[i], Px[i + 1], Px[i + 2], Px[i + 3]);
    }

    public void Set(int x, int y, byte b, byte g, byte r, byte a)
    {
        int i = (y * W + x) * 4;
        Px[i] = b; Px[i + 1] = g; Px[i + 2] = r; Px[i + 3] = a;
    }
}

internal static class ImgOps
{
    /// <summary>双线性缩放。</summary>
    public static Bgra Resize(Bgra src, int dw, int dh)
    {
        var dst = new Bgra(dw, dh);
        double sx = (double)src.W / dw, sy = (double)src.H / dh;
        for (int y = 0; y < dh; y++)
        {
            double fy = (y + 0.5) * sy - 0.5;
            int y0 = (int)Math.Floor(fy);
            double ty = fy - y0;
            int y1 = Math.Clamp(y0 + 1, 0, src.H - 1);
            y0 = Math.Clamp(y0, 0, src.H - 1);
            for (int x = 0; x < dw; x++)
            {
                double fx = (x + 0.5) * sx - 0.5;
                int x0 = (int)Math.Floor(fx);
                double tx = fx - x0;
                int x1 = Math.Clamp(x0 + 1, 0, src.W - 1);
                x0 = Math.Clamp(x0, 0, src.W - 1);

                int i00 = (y0 * src.W + x0) * 4, i10 = (y0 * src.W + x1) * 4;
                int i01 = (y1 * src.W + x0) * 4, i11 = (y1 * src.W + x1) * 4;
                for (int c = 0; c < 4; c++)
                {
                    double v = (src.Px[i00 + c] * (1 - tx) + src.Px[i10 + c] * tx) * (1 - ty)
                             + (src.Px[i01 + c] * (1 - tx) + src.Px[i11 + c] * tx) * ty;
                    dst.Px[(y * dw + x) * 4 + c] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                }
            }
        }
        return dst;
    }

    /// <summary>三次盒式模糊近似高斯（作用在 float RGBA 交错数组上）。</summary>
    public static void Blur(float[] buf, int w, int h, int radius)
    {
        if (radius <= 0) return;
        var tmp = new float[buf.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxH(buf, tmp, w, h, radius);
            BoxV(tmp, buf, w, h, radius);
        }
    }

    private static void BoxH(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (r * 2 + 1);
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int c = 0; c < 4; c++)
            {
                float sum = 0;
                for (int i = -r; i <= r; i++) sum += src[row + Math.Clamp(i, 0, w - 1) * 4 + c];
                for (int x = 0; x < w; x++)
                {
                    dst[row + x * 4 + c] = sum * inv;
                    int add = Math.Clamp(x + r + 1, 0, w - 1);
                    int sub = Math.Clamp(x - r, 0, w - 1);
                    sum += src[row + add * 4 + c] - src[row + sub * 4 + c];
                }
            }
        }
    }

    private static void BoxV(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (r * 2 + 1);
        for (int x = 0; x < w; x++)
        {
            for (int c = 0; c < 4; c++)
            {
                float sum = 0;
                for (int i = -r; i <= r; i++) sum += src[(Math.Clamp(i, 0, h - 1) * w + x) * 4 + c];
                for (int y = 0; y < h; y++)
                {
                    dst[(y * w + x) * 4 + c] = sum * inv;
                    int add = Math.Clamp(y + r + 1, 0, h - 1);
                    int sub = Math.Clamp(y - r, 0, h - 1);
                    sum += src[(add * w + x) * 4 + c] - src[(sub * w + x) * 4 + c];
                }
            }
        }
    }

    public static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    public static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3 - 2 * t);
    }

    /// <summary>去边框：按权重取平均色（用于"跟壁纸走"的染色）。</summary>
    public static (float R, float G, float B) AverageColor(Bgra img, int x0, int y0, int x1, int y1)
    {
        double r = 0, g = 0, b = 0; long n = 0;
        x0 = Math.Clamp(x0, 0, img.W - 1); y0 = Math.Clamp(y0, 0, img.H - 1);
        x1 = Math.Clamp(x1, 0, img.W); y1 = Math.Clamp(y1, 0, img.H);
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2)
            {
                int i = (y * img.W + x) * 4;
                b += img.Px[i]; g += img.Px[i + 1]; r += img.Px[i + 2];
                n++;
            }
        if (n == 0) return (0.5f, 0.5f, 0.5f);
        return ((float)(r / n / 255.0), (float)(g / n / 255.0), (float)(b / n / 255.0));
    }
}
