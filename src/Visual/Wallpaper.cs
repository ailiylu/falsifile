using System.Drawing;
using Microsoft.Win32;
using DeskTidy.Core;

namespace DeskTidy.Visual;

/// <summary>
/// 取"桌面后面那张图"。优先用 Windows 已经处理好的 TranscodedWallpaper（就是屏幕上真正显示的那张），
/// 拿不到再读原始壁纸并按 WallpaperStyle 复现填充算法。
/// 注意：这是"读壁纸文件"，不是抓屏。
/// </summary>
internal sealed class Wallpaper
{
    public Bgra Image { get; }
    public string SourceDescription { get; }
    public bool IsFallback { get; }

    private Wallpaper(Bgra image, string desc, bool fallback)
    {
        Image = image;
        SourceDescription = desc;
        IsFallback = fallback;
    }

    public static Wallpaper Load(int targetW, int targetH)
    {
        // 1) TranscodedWallpaper：Windows 已经按当前显示器处理过的成品
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
            if (File.Exists(p))
            {
                var img = Decode(p);
                if (img is not null) return new Wallpaper(img, "TranscodedWallpaper", false);
            }
        }
        catch (Exception ex) { AppLog.Warn($"读 TranscodedWallpaper 失败：{ex.Message}"); }

        // 2) 原始壁纸 + 填充算法
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var path = key?.GetValue("WallPaper") as string;
            int style = 0, tile = 0;
            int.TryParse(key?.GetValue("WallpaperStyle")?.ToString(), out style);
            int.TryParse(key?.GetValue("TileWallpaper")?.ToString(), out tile);

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var raw = Decode(path);
                if (raw is not null)
                    return new Wallpaper(ApplyStyle(raw, targetW, targetH, style, tile == 1), $"file:{path} style={style}", false);
            }
        }
        catch (Exception ex) { AppLog.Warn($"读壁纸文件失败：{ex.Message}"); }

        // 3) 兜底：注册表里的桌面背景色
        var color = ReadBackgroundColor();
        var solid = new Bgra(targetW, targetH);
        for (int i = 0; i < solid.Px.Length; i += 4)
        {
            solid.Px[i] = color.B; solid.Px[i + 1] = color.G; solid.Px[i + 2] = color.R; solid.Px[i + 3] = 255;
        }
        return new Wallpaper(solid, $"solid:{color}", true);
    }

    private static (byte R, byte G, byte B) ReadBackgroundColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
            var s = key?.GetValue("Background") as string;
            if (!string.IsNullOrWhiteSpace(s))
            {
                var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    return (byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
            }
        }
        catch { }
        return (24, 26, 32);
    }

    private static Bgra? Decode(string path)
    {
        try
        {
            using var bmp = new Bitmap(path);
            var dst = new Bgra(bmp.Width, bmp.Height);
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    IntPtr row = data.Scan0 + y * data.Stride;
                    System.Runtime.InteropServices.Marshal.Copy(row, dst.Px, y * bmp.Width * 4, bmp.Width * 4);
                }
            }
            finally { bmp.UnlockBits(data); }

            // 壁纸没有 alpha，统一置为不透明
            for (int i = 3; i < dst.Px.Length; i += 4) dst.Px[i] = 255;
            return dst;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"壁纸解码失败 {path}：{ex.Message}");
            return null;
        }
    }

    /// <summary>复现 WallpaperStyle：0=居中 2=拉伸 6=适应 10=填充 22=跨屏。</summary>
    private static Bgra ApplyStyle(Bgra src, int w, int h, int style, bool tile)
    {
        var dst = new Bgra(w, h);
        var bg = ReadBackgroundColor();
        for (int i = 0; i < dst.Px.Length; i += 4)
        {
            dst.Px[i] = bg.B; dst.Px[i + 1] = bg.G; dst.Px[i + 2] = bg.R; dst.Px[i + 3] = 255;
        }

        if (tile)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var (b, g, r, _) = src.At(x % src.W, y % src.H);
                    dst.Set(x, y, b, g, r, 255);
                }
            return dst;
        }

        Bgra scaled;
        int ox, oy;
        switch (style)
        {
            case 2: // 拉伸
                scaled = ImgOps.Resize(src, w, h);
                ox = 0; oy = 0;
                break;
            case 6: // 适应
            {
                double s = Math.Min((double)w / src.W, (double)h / src.H);
                int sw = Math.Max(1, (int)(src.W * s)), sh = Math.Max(1, (int)(src.H * s));
                scaled = ImgOps.Resize(src, sw, sh);
                ox = (w - sw) / 2; oy = (h - sh) / 2;
                break;
            }
            case 0: // 居中（原始尺寸）
                scaled = src;
                ox = (w - src.W) / 2; oy = (h - src.H) / 2;
                break;
            default: // 10 填充 / 22 跨屏
            {
                double s = Math.Max((double)w / src.W, (double)h / src.H);
                int sw = Math.Max(1, (int)(src.W * s)), sh = Math.Max(1, (int)(src.H * s));
                scaled = ImgOps.Resize(src, sw, sh);
                ox = (w - sw) / 2; oy = (h - sh) / 2;
                break;
            }
        }

        for (int y = 0; y < scaled.H; y++)
        {
            int dy = y + oy;
            if (dy < 0 || dy >= h) continue;
            for (int x = 0; x < scaled.W; x++)
            {
                int dx = x + ox;
                if (dx < 0 || dx >= w) continue;
                var (b, g, r, _) = scaled.At(x, y);
                dst.Set(dx, dy, b, g, r, 255);
            }
        }
        return dst;
    }
}
