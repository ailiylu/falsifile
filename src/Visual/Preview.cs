using System.Drawing;
using System.Drawing.Imaging;
using DeskTidy.Core;

namespace DeskTidy.Visual;

internal static class Preview
{
    public static int RenderAll(string outDir, string iconPath)
    {
        Directory.CreateDirectory(outDir);
        var icon = ShellIcon.Load(iconPath, 256);
        AppLog.Info($"preview icon={(icon is null ? "null" : $"{icon.W}x{icon.H}")} path={iconPath}");

        int sw = 2880, sh = 1800;
        var wall = Wallpaper.Load(sw, sh);
        AppLog.Info($"preview wallpaper={wall.SourceDescription} fallback={wall.IsFallback} size={wall.Image.W}x{wall.Image.H}");

        var outputs = new List<string>();

        // 1) 真实壁纸 @200%
        outputs.Add(Render(outDir, "1_real_200.png", wall.Image, 2.0f, icon, "真实壁纸 · 200%"));

        // 2) 真实壁纸 @100%（同一块壁纸，按 1x 渲染）
        var half = ImgOps.Resize(wall.Image, sw / 2, sh / 2);
        outputs.Add(Render(outDir, "2_real_100.png", half, 1.0f, icon, "真实壁纸 · 100%"));

        // 3) 深色底
        outputs.Add(Render(outDir, "3_dark_200.png", Gradient(sw, sh, (18, 20, 26), (44, 48, 62)), 2.0f, icon, "深色底 · 200%"));

        // 4) 浅色底
        outputs.Add(Render(outDir, "4_light_200.png", Gradient(sw, sh, (238, 238, 240), (208, 214, 224)), 2.0f, icon, "浅色底 · 200%"));

        // 5) 同一块玻璃放在壁纸的亮部/暗部/彩色部，看材质是否成立
        outputs.Add(RenderSheet(outDir, "5_variety_200.png", wall.Image, icon, 2.0f));

        foreach (var p in outputs) AppLog.Info($"preview wrote {p}");
        return 0;
    }

    private static string Render(string dir, string name, Bgra backdrop, float scale, Bgra? icon, string label)
    {
        int cx = backdrop.W / 2, cy = backdrop.H / 2;
        var tile = GlassTile.Render(backdrop, cx, cy, scale, icon);

        int cropW = Math.Min(backdrop.W, (int)(tile.Sprite.W * 2.6f));
        int cropH = Math.Min(backdrop.H, (int)(tile.Sprite.H * 1.9f));
        int x0 = Math.Clamp(cx - cropW / 2, 0, Math.Max(0, backdrop.W - cropW));
        int y0 = Math.Clamp(cy - cropH / 2, 0, Math.Max(0, backdrop.H - cropH));

        var canvas = new Bgra(cropW, cropH);
        for (int y = 0; y < cropH; y++)
            Buffer.BlockCopy(backdrop.Px, ((y0 + y) * backdrop.W + x0) * 4, canvas.Px, y * cropW * 4, cropW * 4);

        // 精灵中心对齐到画布中心
        int sx = cropW / 2 - tile.Sprite.W / 2;
        int sy = cropH / 2 - tile.Sprite.H / 2;
        Blend(canvas, tile.Sprite, sx, sy);

        var path = Path.Combine(dir, name);
        SavePng(canvas, path);
        return path;
    }

    /// <summary>把同一块玻璃放到壁纸的暗部 / 亮部 / 彩色部，横向拼一张对照图。</summary>
    private static string RenderSheet(string dir, string name, Bgra wall, Bgra? icon, float scale)
    {
        var pts = PickSamplePoints(wall);
        int cw = 360, ch = 360;
        var sheet = new Bgra(cw * pts.Count, ch);

        for (int i = 0; i < pts.Count; i++)
        {
            var (px, py) = pts[i];
            var tile = GlassTile.Render(wall, px, py, scale, icon);
            int x0 = Math.Clamp(px - cw / 2, 0, Math.Max(0, wall.W - cw));
            int y0 = Math.Clamp(py - ch / 2, 0, Math.Max(0, wall.H - ch));

            for (int y = 0; y < ch; y++)
                Buffer.BlockCopy(wall.Px, ((y0 + y) * wall.W + x0) * 4,
                    sheet.Px, ((y * sheet.W) + i * cw) * 4, cw * 4);

            Blend(sheet, tile.Sprite,
                i * cw + cw / 2 - tile.Sprite.W / 2,
                ch / 2 - tile.Sprite.H / 2);
        }

        var path = Path.Combine(dir, name);
        SavePng(sheet, path);
        return path;
    }

    /// <summary>在壁纸上找一个暗点、一个亮点、一个高饱和点。</summary>
    private static List<(int X, int Y)> PickSamplePoints(Bgra img)
    {
        var best = new (int X, int Y, double Score)[3];
        for (int i = 0; i < 3; i++) best[i] = (img.W / 2, img.H / 2, double.NaN);

        for (int gy = 2; gy < 9; gy++)
            for (int gx = 1; gx < 11; gx++)
            {
                int x = img.W * gx / 12, y = img.H * gy / 10;
                var (b, g, r, _) = img.At(x, y);
                double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                double mx = Math.Max(r, Math.Max(g, b)) / 255.0;
                double mn = Math.Min(r, Math.Min(g, b)) / 255.0;
                double sat = mx - mn;

                Consider(0, x, y, -lum, ref best);      // 最暗
                Consider(1, x, y, lum, ref best);       // 最亮
                Consider(2, x, y, sat * (1 - Math.Abs(lum - 0.5)), ref best); // 彩色且不过曝
            }

        return best.Select(b => (b.X, b.Y)).ToList();

        static void Consider(int slot, int x, int y, double score, ref (int X, int Y, double Score)[] best)
        {
            if (double.IsNaN(best[slot].Score) || score > best[slot].Score)
                best[slot] = (x, y, score);
        }
    }

    private static void Blend(Bgra dst, Bgra src, int ox, int oy)
    {
        // src 是预乘 alpha；dst 不透明
        for (int y = 0; y < src.H; y++)
        {
            int dy = oy + y;
            if (dy < 0 || dy >= dst.H) continue;
            for (int x = 0; x < src.W; x++)
            {
                int dx = ox + x;
                if (dx < 0 || dx >= dst.W) continue;
                int si = (y * src.W + x) * 4;
                int di = (dy * dst.W + dx) * 4;
                float a = src.Px[si + 3] / 255f;
                float inv = 1f - a;
                dst.Px[di]     = (byte)Math.Clamp((int)Math.Round(src.Px[si]     + dst.Px[di]     * inv), 0, 255);
                dst.Px[di + 1] = (byte)Math.Clamp((int)Math.Round(src.Px[si + 1] + dst.Px[di + 1] * inv), 0, 255);
                dst.Px[di + 2] = (byte)Math.Clamp((int)Math.Round(src.Px[si + 2] + dst.Px[di + 2] * inv), 0, 255);
                dst.Px[di + 3] = 255;
            }
        }
    }

    private static Bgra Gradient(int w, int h, (byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        var img = new Bgra(w, h);
        for (int y = 0; y < h; y++)
        {
            float t = (float)y / (h - 1);
            for (int x = 0; x < w; x++)
            {
                // 对角线渐变，方便看折射和投影
                float tt = ImgOps.Clamp01(t * 0.8f + (float)x / w * 0.2f);
                int i = (y * w + x) * 4;
                img.Px[i]     = (byte)(a.B + (b.B - a.B) * tt);
                img.Px[i + 1] = (byte)(a.G + (b.G - a.G) * tt);
                img.Px[i + 2] = (byte)(a.R + (b.R - a.R) * tt);
                img.Px[i + 3] = 255;
            }
        }
        return img;
    }

    private static void SavePng(Bgra img, string path)
    {
        using var bmp = new Bitmap(img.W, img.H, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, img.W, img.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < img.H; y++)
                System.Runtime.InteropServices.Marshal.Copy(img.Px, y * img.W * 4, data.Scan0 + y * data.Stride, img.W * 4);
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(path, ImageFormat.Png);
    }
}
