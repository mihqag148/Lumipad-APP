using System.IO;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using DrawingImaging = System.Drawing.Imaging;

namespace LumiPad.App;

/// <summary>
/// PIXEL PRO-only app preview for the firmware-resident factory visuals.
/// This renderer follows the aqua / cyan / teal reference used by firmware
/// 1.8.10 and intentionally does not affect any RYNOR ONE media path.
/// </summary>
public static class PixelProFactoryVisual
{
    public const int Width = 480;
    public const int Height = 320;
    public const int Fps = 20;

    public static byte[] CreatePng(
        long elapsedMs = 0,
        bool animated = false)
    {
        using var bitmap =
            new Drawing.Bitmap(
                Width,
                Height,
                DrawingImaging.PixelFormat.Format32bppPArgb);

        using Drawing.Graphics graphics =
            Drawing.Graphics.FromImage(bitmap);

        graphics.SmoothingMode =
            Drawing2D.SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode =
            Drawing2D.PixelOffsetMode.HighQuality;
        graphics.CompositingQuality =
            Drawing2D.CompositingQuality.HighQuality;

        DrawSky(
            graphics,
            elapsedMs,
            animated);

        float phase =
            animated
                ? elapsedMs * 0.00105f
                : 0.62f;

        // Back atmospheric sheet.
        DrawRibbon(
            graphics,
            118,
            30,
            72,
            phase * 0.42f,
            0.78f,
            Drawing.Color.FromArgb(178, 73, 190, 218),
            Drawing.Color.FromArgb(218, 186, 242, 237),
            Drawing.Color.FromArgb(170, 224, 255, 250));

        // Deep teal hill on the left/middle.
        DrawRibbon(
            graphics,
            160,
            37,
            92,
            phase * -0.55f + 1.55f,
            0.92f,
            Drawing.Color.FromArgb(246, 0, 60, 104),
            Drawing.Color.FromArgb(238, 20, 204, 177),
            Drawing.Color.FromArgb(185, 164, 255, 236));

        // Bright cyan middle layer.
        DrawRibbon(
            graphics,
            214,
            28,
            72,
            phase * 0.66f + 2.18f,
            1.08f,
            Drawing.Color.FromArgb(238, 0, 93, 171),
            Drawing.Color.FromArgb(232, 41, 196, 220),
            Drawing.Color.FromArgb(180, 196, 255, 251));

        // Thin luminous turquoise ribbon crossing the lower-middle region.
        DrawRibbon(
            graphics,
            244,
            20,
            44,
            phase * 0.34f + 0.82f,
            1.22f,
            Drawing.Color.FromArgb(178, 0, 117, 159),
            Drawing.Color.FromArgb(206, 50, 225, 205),
            Drawing.Color.FromArgb(190, 218, 255, 250));

        // Foreground navy sheet gives the reference its deep lower edge.
        DrawRibbon(
            graphics,
            276,
            30,
            96,
            phase * -0.48f + 0.28f,
            0.82f,
            Drawing.Color.FromArgb(255, 0, 50, 111),
            Drawing.Color.FromArgb(255, 0, 27, 70),
            Drawing.Color.FromArgb(168, 119, 238, 232));

        using var output =
            new MemoryStream();

        bitmap.Save(
            output,
            DrawingImaging.ImageFormat.Png);

        return output.ToArray();
    }

    private static void DrawSky(
        Drawing.Graphics graphics,
        long elapsedMs,
        bool animated)
    {
        using var background =
            new Drawing2D.LinearGradientBrush(
                new Drawing.Rectangle(
                    0,
                    0,
                    Width,
                    Height),
                Drawing.Color.FromArgb(
                    8,
                    94,
                    160),
                Drawing.Color.FromArgb(
                    123,
                    218,
                    234),
                32.0f);

        graphics.FillRectangle(
            background,
            0,
            0,
            Width,
            Height);

        float drift =
            animated
                ? MathF.Sin(
                    elapsedMs *
                    0.00058f) *
                  26.0f
                : 0.0f;

        using var glowPath =
            new Drawing2D.GraphicsPath();

        glowPath.AddEllipse(
            238 + drift,
            -72,
            330,
            255);

        using var glow =
            new Drawing2D.PathGradientBrush(
                glowPath)
            {
                CenterPoint =
                    new Drawing.PointF(
                        390 + drift,
                        70),
                CenterColor =
                    Drawing.Color.FromArgb(
                        210,
                        247,
                        255,
                        251),
                SurroundColors =
                [
                    Drawing.Color.FromArgb(
                        0,
                        247,
                        255,
                        251)
                ]
            };

        graphics.FillPath(
            glow,
            glowPath);

        using var haze =
            new Drawing2D.LinearGradientBrush(
                new Drawing.Rectangle(
                    0,
                    28,
                    Width,
                    160),
                Drawing.Color.FromArgb(
                    18,
                    135,
                    220,
                    235),
                Drawing.Color.FromArgb(
                    92,
                    232,
                    252,
                    248),
                Drawing2D.LinearGradientMode.Vertical);

        graphics.FillRectangle(
            haze,
            0,
            28,
            Width,
            160);
    }

    private static void DrawRibbon(
        Drawing.Graphics graphics,
        float baseY,
        float amplitude,
        float thickness,
        float phase,
        float frequency,
        Drawing.Color topColor,
        Drawing.Color bottomColor,
        Drawing.Color edgeColor)
    {
        Drawing.PointF[] top =
            CreateWavePoints(
                baseY,
                amplitude,
                phase,
                frequency);

        Drawing.PointF[] bottom =
            CreateWavePoints(
                baseY +
                thickness,
                amplitude *
                0.72f,
                phase +
                0.72f,
                frequency *
                0.93f);

        Array.Reverse(
            bottom);

        using var path =
            new Drawing2D.GraphicsPath();

        path.AddCurve(
            top,
            0.28f);

        path.AddLine(
            top[^1],
            bottom[0]);

        path.AddCurve(
            bottom,
            0.28f);

        path.CloseFigure();

        float minY =
            Math.Max(
                -20.0f,
                baseY -
                amplitude -
                12.0f);

        float maxY =
            Math.Min(
                Height +
                80.0f,
                baseY +
                thickness +
                amplitude +
                24.0f);

        using var fill =
            new Drawing2D.LinearGradientBrush(
                new Drawing.RectangleF(
                    0,
                    minY,
                    Width,
                    Math.Max(
                        1.0f,
                        maxY -
                        minY)),
                topColor,
                bottomColor,
                Drawing2D.LinearGradientMode.Vertical);

        graphics.FillPath(
            fill,
            path);

        using var edge =
            new Drawing.Pen(
                edgeColor,
                1.15f);

        using var edgePath =
            new Drawing2D.GraphicsPath();

        edgePath.AddCurve(
            top,
            0.28f);

        graphics.DrawPath(
            edge,
            edgePath);

        // A broad translucent shine just below the leading edge keeps the
        // preview close to the soft glassy highlight in the source image.
        using var shine =
            new Drawing.Pen(
                Drawing.Color.FromArgb(
                    34,
                    255,
                    255,
                    255),
                6.0f);

        graphics.DrawPath(
            shine,
            edgePath);
    }

    private static Drawing.PointF[] CreateWavePoints(
        float baseY,
        float amplitude,
        float phase,
        float frequency)
    {
        const int step = 48;
        int count =
            Width /
            step +
            4;

        var points =
            new Drawing.PointF[count];

        for (int i = 0;
             i < count;
             i++)
        {
            float x =
                -step +
                i *
                step;

            float normalized =
                x /
                Width *
                MathF.PI *
                2.0f;

            float y =
                baseY +
                MathF.Sin(
                    normalized *
                    frequency +
                    phase) *
                amplitude +
                MathF.Sin(
                    normalized *
                    frequency *
                    1.91f -
                    phase *
                    0.47f +
                    1.2f) *
                amplitude *
                0.22f;

            points[i] =
                new Drawing.PointF(
                    x,
                    y);
        }

        return points;
    }
}
