using System.Runtime.InteropServices;
using DeskTidy.Core;

namespace DeskTidy.Visual;

/// <summary>
/// 取系统真实图标（不是自己画的）。走 IShellItemImageFactory.GetImage，
/// 直接拿 Shell 渲染好的 256px 位图，自带透明通道和所有叠加标记。
/// </summary>
internal static class ShellIcon
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")] private static extern int GetObjectW(IntPtr h, int c, ref BITMAP pv);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
        byte[] bits, ref BITMAPINFOHEADER bi, uint usage);

    private const int SIIGBF_BIGGERSIZEOK = 0x01;
    private const int SIIGBF_ICONONLY = 0x04;

    public static Bgra? Load(string path, int px)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var obj);
            var factory = (IShellItemImageFactory)obj;
            factory.GetImage(new SIZE { cx = px, cy = px }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm);
            if (hbm == IntPtr.Zero) return null;
            return FromHBitmap(hbm);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"取图标失败 {path}：{ex.Message}");
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
        }
    }

    private static Bgra? FromHBitmap(IntPtr hbm)
    {
        var bm = new BITMAP();
        if (GetObjectW(hbm, Marshal.SizeOf<BITMAP>(), ref bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        if (w <= 0 || h <= 0) return null;

        var bi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // 负值 = 自上而下
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        var buf = new byte[w * h * 4];
        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            IntPtr old = SelectObject(hdc, hbm);
            int lines = GetDIBits(hdc, hbm, 0, (uint)h, buf, ref bi, 0);
            SelectObject(hdc, old);
            if (lines == 0) return null;
        }
        finally { DeleteDC(hdc); }

        // 某些图标返回全透明 alpha，兜底按非 0 填充 alpha
        bool anyAlpha = false;
        for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha)
            for (int i = 0; i < buf.Length; i += 4)
                buf[i + 3] = (byte)((buf[i] | buf[i + 1] | buf[i + 2]) != 0 ? 255 : 0);

        return new Bgra(w, h, buf);
    }
}
