using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using DeskTidy.Interop;

namespace DeskTidy.UI;

/// <summary>
/// 校准渲染：把推算出来的图标矩形画在桌面上。
/// 品红点 = 该条目的精确坐标点；青色框 = 该条目所在的单元格（GetSpacing）。
/// 你只要看一眼"点是不是正好落在图标左上角、框是不是框住了整格"。
/// </summary>
internal static class CalibrationRenderer
{
    public static void Draw(ID2D1DeviceContext ctx, IReadOnlyList<(NativeMethods.POINT Point, int CellW, int CellH)> cells,
        NativeMethods.RECT origin, int dpi, int itemCount)
    {
        using var magenta = ctx.CreateSolidColorBrush(new Color4(1f, 0f, 0.85f, 1f));
        using var cyan = ctx.CreateSolidColorBrush(new Color4(0f, 0.9f, 1f, 0.9f));
        using var green = ctx.CreateSolidColorBrush(new Color4(0.2f, 1f, 0.3f, 1f));

        // 画布原点标记（左上角），验证画布自身对齐
        ctx.FillRectangle(new RawRectF(0, 0, 36, 36), green);

        foreach (var (pt, cw, ch) in cells)
        {
            float x = pt.X - origin.Left;
            float y = pt.Y - origin.Top;

            // 单元格框
            ctx.DrawRectangle(new RawRectF(x, y, x + cw, y + ch), cyan, 1.5f);

            // 精确点：12x12 实心 + 十字
            ctx.FillRectangle(new RawRectF(x - 6, y - 6, x + 6, y + 6), magenta);
            ctx.DrawLine(new System.Numerics.Vector2(x - 18, y), new System.Numerics.Vector2(x + 18, y), magenta, 1f);
            ctx.DrawLine(new System.Numerics.Vector2(x, y - 18), new System.Numerics.Vector2(x, y + 18), magenta, 1f);
        }
    }
}
