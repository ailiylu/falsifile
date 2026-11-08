using System.Drawing;
using System.Drawing.Imaging;
using DeskTidy.Core;

namespace DeskTidy.Visual;

/// <summary>
/// 给桌面快捷方式生成多尺寸 .ico：
///   **透明背景** + 系统文件夹图标 + 一层柔和投影。
/// 不再有黑色底板 —— 桌面上看到的就是"一个文件夹"，只是比系统图标更有浮起来的质感。
/// </summary>
internal static class IconWriter
{
    private static readonly int[] Sizes = { 256, 128, 64, 48, 32, 16 };

    public static string Write(string icoPath, Bgra folderIcon)
    {
        var pngs = new List<(int Size, byte[] Data)>();

        foreach (var sz in Sizes)
        {
            try
            {
                var img = Render(sz, folderIcon);
                pngs.Add((sz, ToPng(img)));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"生成 {sz}px 图标失败：{ex.Message}");
            }
        }

        if (pngs.Count == 0) throw new InvalidOperationException("一个尺寸都没生成出来");

        Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
        using var fs = File.Create(icoPath);
        using var w = new BinaryWriter(fs);

        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)pngs.Count);

        int offset = 6 + 16 * pngs.Count;
        foreach (var (size, data) in pngs)
        {
            byte b = size >= 256 ? (byte)0 : (byte)size;
            w.Write(b);
            w.Write(b);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write((uint)data.Length);
            w.Write((uint)offset);
            offset += data.Length;
        }

        foreach (var (_, data) in pngs) w.Write(data);
        w.Flush();

        AppLog.Info($"图标已生成 {icoPath}（{string.Join(",", pngs.Select(p => p.Size))}）");
        return icoPath;
    }

    /// <summary>透明底：模糊 alpha 当投影，再把文件夹本身压在中间。</summary>
    private static Bgra Render(int sz, Bgra folderIcon)
    {
        // 文件夹占 90%，四周留一点给投影
        int inset = Math.Max(1, (int)Math.Round(sz * 0.05));
        int glyph = Math.Max(4, sz - inset * 2);
        var scaled = ImgOps.Resize(folderIcon, glyph, glyph);

        int n = glyph * glyph;
        var mask = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            mask[i * 4] = 0; mask[i * 4 + 1] = 0; mask[i * 4 + 2] = 0;
            mask[i * 4 + 3] = scaled.Px[i * 4 + 3] / 255f;
        }
        ImgOps.Blur(mask, glyph, glyph, Math.Max(1, sz / 22));

        int dy = Math.Max(1, (int)Math.Round(sz * 0.03));
        var acc = new float[sz * sz * 4];   // 预乘 RGBA

        // 投影
        for (int y = 0; y < glyph; y++)
        {
            int py = y + inset + dy;
            if (py < 0 || py >= sz) continue;
            for (int x = 0; x < glyph; x++)
            {
                float a = mask[(y * glyph + x) * 4 + 3] * 0.42f;
                if (a <= 0.002f) continue;
                int px = x + inset;
                if (px < 0 || px >= sz) continue;
                int o = (py * sz + px) * 4;
                float inv = 1f - a;
                acc[o] *= inv; acc[o + 1] *= inv; acc[o + 2] *= inv;
                acc[o + 3] = a + acc[o + 3] * inv;
            }
        }

        // 文件夹本体（预乘后叠加）
        for (int y = 0; y < glyph; y++)
        {
            int py = y + inset;
            if (py < 0 || py >= sz) continue;
            for (int x = 0; x < glyph; x++)
            {
                int i = (y * glyph + x) * 4;
                float a = scaled.Px[i + 3] / 255f;
                if (a <= 0.002f) continue;
                int px = x + inset;
                if (px < 0 || px >= sz) continue;
                int o = (py * sz + px) * 4;
                float inv = 1f - a;
                acc[o] = scaled.Px[i + 2] / 255f * a + acc[o] * inv;
                acc[o + 1] = scaled.Px[i + 1] / 255f * a + acc[o + 1] * inv;
                acc[o + 2] = scaled.Px[i] / 255f * a + acc[o + 2] * inv;
                acc[o + 3] = a + acc[o + 3] * inv;
            }
        }

        var img = new Bgra(sz, sz);
        for (int i = 0; i < sz * sz; i++)
        {
            int o = i * 4;
            img.Px[o] = ToByte(acc[o + 2]);     // B
            img.Px[o + 1] = ToByte(acc[o + 1]); // G
            img.Px[o + 2] = ToByte(acc[o]);     // R
            img.Px[o + 3] = ToByte(acc[o + 3]); // A
        }
        return img;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)Math.Round(v * 255f), 0, 255);

    /// <summary>预乘 → 直通，编码成 PNG 字节。</summary>
    private static byte[] ToPng(Bgra img)
    {
        using var bmp = new Bitmap(img.W, img.H, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, img.W, img.H),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < img.H; y++)
            {
                for (int x = 0; x < img.W; x++)
                {
                    int i = (y * img.W + x) * 4;
                    byte a = img.Px[i + 3];
                    int o = y * data.Stride + x * 4;
                    if (a == 0)
                    {
                        System.Runtime.InteropServices.Marshal.WriteInt32(data.Scan0, o, 0);
                        continue;
                    }
                    byte b = (byte)Math.Min(255, img.Px[i] * 255 / a);
                    byte g = (byte)Math.Min(255, img.Px[i + 1] * 255 / a);
                    byte r = (byte)Math.Min(255, img.Px[i + 2] * 255 / a);
                    System.Runtime.InteropServices.Marshal.WriteInt32(data.Scan0, o,
                        b | (g << 8) | (r << 16) | (a << 24));
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
