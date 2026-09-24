using System.IO;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using DrawingImaging = System.Drawing.Imaging;

namespace LumiPad.App;

/// <summary>
/// PIXEL PRO-only preview for the firmware-resident red/orange dune
/// screensaver introduced by firmware 1.9.7.
/// </summary>
public static class PixelProFactoryVisual
{
    public const string AssetId =
        "DUNE_RED_ORANGE_ANIM";

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

        using (
            var baseStream =
                new MemoryStream(
                    PixelProFactoryMenuVisual.CreateJpeg(),
                    writable: false))
        using (
            var source =
                new Drawing.Bitmap(
                    baseStream))
        {
            graphics.DrawImage(
                source,
                new Drawing.Rectangle(
                    0,
                    0,
                    Width,
                    Height));
        }

        float phase =
            animated
                ? elapsedMs * 0.00072f
                : 0.64f;

        DrawHorizonGlow(
            graphics,
            phase);

        DrawAnimatedRidge(
            graphics,
            baseY: 164,
            amplitude: 20,
            phase: phase * 0.52f + 0.55f,
            frequency: 0.92f,
            width: 2.8f,
            Drawing.Color.FromArgb(
                124,
                255,
                190,
                70));

        DrawAnimatedRidge(
            graphics,
            baseY: 218,
            amplitude: 25,
            phase: -phase * 0.46f + 1.65f,
            frequency: 0.84f,
            width: 2.2f,
            Drawing.Color.FromArgb(
                116,
                255,
                92,
                18));

        DrawAnimatedRidge(
            graphics,
            baseY: 272,
            amplitude: 23,
            phase: phase * 0.38f + 2.75f,
            frequency: 0.77f,
            width: 1.8f,
            Drawing.Color.FromArgb(
                88,
                255,
                72,
                16));

        using var output =
            new MemoryStream();

        bitmap.Save(
            output,
            DrawingImaging.ImageFormat.Png);

        return output.ToArray();
    }

    private static void DrawHorizonGlow(
        Drawing.Graphics graphics,
        float phase)
    {
        float drift =
            MathF.Sin(
                phase * 0.41f) *
            24.0f;

        using var glowPath =
            new Drawing2D.GraphicsPath();

        glowPath.AddEllipse(
            242 + drift,
            72,
            220,
            142);

        using var glow =
            new Drawing2D.PathGradientBrush(
                glowPath)
            {
                CenterPoint =
                    new Drawing.PointF(
                        342 + drift,
                        145),
                CenterColor =
                    Drawing.Color.FromArgb(
                        70,
                        255,
                        182,
                        72),
                SurroundColors =
                [
                    Drawing.Color.FromArgb(
                        0,
                        255,
                        92,
                        18)
                ]
            };

        graphics.FillPath(
            glow,
            glowPath);
    }

    private static void DrawAnimatedRidge(
        Drawing.Graphics graphics,
        float baseY,
        float amplitude,
        float phase,
        float frequency,
        float width,
        Drawing.Color color)
    {
        const int step = 32;

        var points =
            new List<Drawing.PointF>();

        for (int x = -step;
             x <= Width + step;
             x += step)
        {
            float normalized =
                x /
                (float)Width *
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
                    0.44f +
                    1.1f) *
                amplitude *
                0.20f;

            points.Add(
                new Drawing.PointF(
                    x,
                    y));
        }

        using var path =
            new Drawing2D.GraphicsPath();

        path.AddCurve(
            points.ToArray(),
            0.30f);

        using var broad =
            new Drawing.Pen(
                Drawing.Color.FromArgb(
                    Math.Max(
                        16,
                        color.A / 3),
                    color.R,
                    color.G,
                    color.B),
                width * 4.0f);

        graphics.DrawPath(
            broad,
            path);

        using var edge =
            new Drawing.Pen(
                color,
                width);

        graphics.DrawPath(
            edge,
            path);
    }
}
