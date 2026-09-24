using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using DrawingImaging = System.Drawing.Imaging;

namespace LumiPad.App;

/// <summary>
/// App-side preview for the firmware-resident PIXEL PRO factory visuals.
/// The geometry and palette mirror firmware 1.8.9 so clearing user media in
/// LumiPad immediately reveals the same default wallpaper/screensaver.
/// </summary>
public static class PixelProFactoryVisual
{
    public const int Width = 480;
    public const int Height = 320;
    public const int Fps = 12;

    private static readonly Drawing.Color[] Palette =
    [
        Drawing.Color.FromArgb(176, 220, 255),
        Drawing.Color.FromArgb(77, 177, 255),
        Drawing.Color.FromArgb(29, 132, 246),
        Drawing.Color.FromArgb(66, 89, 238),
        Drawing.Color.FromArgb(123, 76, 238),
        Drawing.Color.FromArgb(226, 94, 205)
    ];

    public static byte[] CreatePng(
        long elapsedMs = 0,
        bool animated = false)
    {
        using var bitmap =
            new Drawing.Bitmap(
                Width,
                Height,
                DrawingImaging.PixelFormat.Format32bppArgb);

        using Drawing.Graphics graphics =
            Drawing.Graphics.FromImage(bitmap);

        graphics.SmoothingMode =
            Drawing2D.SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode =
            Drawing2D.PixelOffsetMode.HighQuality;

        float phase =
            animated
                ? elapsedMs * 0.00115f
                : 0.35f;

        const int bandCount = 32;

        for (int band = 0; band < bandCount; band++)
        {
            int y0 =
                band * Height /
                bandCount;
            int y1 =
                (band + 1) * Height /
                bandCount;

            float position =
                band /
                (float)(bandCount - 1) *
                (Palette.Length - 1);

            int index =
                Math.Min(
                    (int)position,
                    Palette.Length - 2);

            float local =
                position -
                index;

            Drawing.Color color =
                Lerp(
                    Palette[index],
                    Palette[index + 1],
                    local);

            using var brush =
                new Drawing.SolidBrush(color);

            graphics.FillRectangle(
                brush,
                0,
                y0,
                Width,
                Math.Max(
                    1,
                    y1 - y0));
        }

        DrawOrb(
            graphics,
            34 +
            (int)(MathF.Sin(
                phase * 0.45f) * 7.0f),
            91,
            70,
            Drawing.Color.FromArgb(255, 155, 126),
            Drawing.Color.FromArgb(255, 188, 154),
            Drawing.Color.FromArgb(255, 226, 207));

        DrawWave(
            graphics,
            112,
            26,
            52,
            phase * 0.75f,
            0.015f,
            Drawing.Color.FromArgb(255, 128, 178),
            Drawing.Color.FromArgb(255, 209, 229));

        DrawWave(
            graphics,
            165,
            32,
            54,
            phase * 0.95f + 1.35f,
            0.018f,
            Drawing.Color.FromArgb(179, 73, 238),
            Drawing.Color.FromArgb(230, 190, 255));

        DrawWave(
            graphics,
            218,
            29,
            58,
            phase * 1.10f + 2.25f,
            0.014f,
            Drawing.Color.FromArgb(36, 104, 244),
            Drawing.Color.FromArgb(151, 203, 255));

        DrawWave(
            graphics,
            266,
            20,
            50,
            phase * 0.85f + 0.75f,
            0.021f,
            Drawing.Color.FromArgb(54, 205, 238),
            Drawing.Color.FromArgb(195, 246, 255));

        int orbX =
            385 +
            (int)(MathF.Sin(
                phase * 0.55f) * 14.0f);

        int orbY =
            72 +
            (int)(MathF.Cos(
                phase * 0.48f) * 10.0f);

        DrawOrb(
            graphics,
            orbX,
            orbY,
            42,
            Drawing.Color.FromArgb(77, 140, 250),
            Drawing.Color.FromArgb(117, 193, 255),
            Drawing.Color.FromArgb(219, 243, 255));

        DrawOrb(
            graphics,
            320 +
            (int)(MathF.Cos(
                phase * 0.68f) * 10.0f),
            252 +
            (int)(MathF.Sin(
                phase * 0.60f) * 7.0f),
            24,
            Drawing.Color.FromArgb(112, 105, 245),
            Drawing.Color.FromArgb(137, 205, 255),
            Drawing.Color.FromArgb(232, 246, 255));

        using var output =
            new MemoryStream();

        bitmap.Save(
            output,
            DrawingImaging.ImageFormat.Png);

        return output.ToArray();
    }

    private static Drawing.Color Lerp(
        Drawing.Color a,
        Drawing.Color b,
        float amount)
    {
        amount =
            Math.Clamp(
                amount,
                0.0f,
                1.0f);

        static int Channel(
            int from,
            int to,
            float t) =>
            Math.Clamp(
                (int)Math.Round(
                    from +
                    (to - from) *
                    t),
                0,
                255);

        return Drawing.Color.FromArgb(
            Channel(
                a.R,
                b.R,
                amount),
            Channel(
                a.G,
                b.G,
                amount),
            Channel(
                a.B,
                b.B,
                amount));
    }

    private static void DrawWave(
        Drawing.Graphics graphics,
        int baseY,
        int amplitude,
        int thickness,
        float phase,
        float frequency,
        Drawing.Color color,
        Drawing.Color highlight)
    {
        const int step = 30;

        using var brush =
            new Drawing.SolidBrush(color);

        using var pen =
            new Drawing.Pen(
                highlight,
                1.0f);

        for (int x = 0;
             x < Width - 1;
             x += step)
        {
            int x1 =
                Math.Min(
                    x + step,
                    Width - 1);

            int y0 =
                baseY +
                (int)(MathF.Sin(
                    x * frequency +
                    phase) *
                    amplitude);

            int y1 =
                baseY +
                (int)(MathF.Sin(
                    x1 * frequency +
                    phase) *
                    amplitude);

            Drawing.Point[] polygon =
            [
                new(x, y0),
                new(x1, y1),
                new(x1, y1 + thickness),
                new(x, y0 + thickness)
            ];

            graphics.FillPolygon(
                brush,
                polygon);

            graphics.DrawLine(
                pen,
                x,
                y0,
                x1,
                y1);
        }
    }

    private static void DrawOrb(
        Drawing.Graphics graphics,
        int cx,
        int cy,
        int radius,
        Drawing.Color outer,
        Drawing.Color inner,
        Drawing.Color shine)
    {
        using var outerBrush =
            new Drawing.SolidBrush(outer);

        using var innerBrush =
            new Drawing.SolidBrush(inner);

        using var shineBrush =
            new Drawing.SolidBrush(shine);

        using var shinePen =
            new Drawing.Pen(
                shine,
                1.0f);

        graphics.FillEllipse(
            outerBrush,
            cx - radius,
            cy - radius,
            radius * 2,
            radius * 2);

        int innerRadius =
            Math.Max(
                2,
                radius * 3 / 4);

        int innerCx =
            cx -
            radius / 6;

        int innerCy =
            cy -
            radius / 6;

        graphics.FillEllipse(
            innerBrush,
            innerCx - innerRadius,
            innerCy - innerRadius,
            innerRadius * 2,
            innerRadius * 2);

        graphics.DrawEllipse(
            shinePen,
            cx - radius,
            cy - radius,
            radius * 2,
            radius * 2);

        int shineRadius =
            Math.Max(
                2,
                radius / 8);

        int shineCx =
            cx -
            radius / 3;

        int shineCy =
            cy -
            radius / 3;

        graphics.FillEllipse(
            shineBrush,
            shineCx - shineRadius,
            shineCy - shineRadius,
            shineRadius * 2,
            shineRadius * 2);
    }
}
