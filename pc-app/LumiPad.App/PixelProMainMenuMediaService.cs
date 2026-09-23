using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace LumiPad.App;

public static class PixelProMainMenuMediaService
{
    public const int BackgroundWidth = 480;
    public const int BackgroundHeight = 320;
    public const int BackgroundMaxBytes = 96 * 1024;

    // eezBotFun-style 8-key layout uses native 96x96 icon canvases. The
    // custom PXI1 payload stores RGB565 pixels plus a transparent color key,
    // so aspect-ratio padding shows the wallpaper instead of a black JPEG box.
    public const int IconWidth = 96;
    public const int IconHeight = 96;
    public const int IconMaxBytes = 24 * 1024;
    public const ushort IconTransparent565 = 0xF81F;

    public static byte[] CreateBackgroundJpeg(
        string path,
        int blurPercent,
        int opacityPercent,
        ScreensaverScaleMode scaleMode)
    {
        ValidateStaticImage(path);

        blurPercent =
            Math.Clamp(blurPercent, 0, 100);

        opacityPercent =
            Math.Clamp(opacityPercent, 0, 100);

        if (scaleMode is not (
            ScreensaverScaleMode.Fill or
            ScreensaverScaleMode.Fit or
            ScreensaverScaleMode.Stretch))
        {
            scaleMode =
                ScreensaverScaleMode.Fill;
        }

        using var source =
            new Bitmap(path);

        using var composed =
            new Bitmap(
                BackgroundWidth,
                BackgroundHeight,
                PixelFormat.Format24bppRgb);

        using (Graphics g = Graphics.FromImage(composed))
        {
            g.Clear(Color.Black);
            ConfigureHighQuality(g);

            if (scaleMode == ScreensaverScaleMode.Stretch)
            {
                g.DrawImage(
                    source,
                    new Rectangle(
                        0,
                        0,
                        BackgroundWidth,
                        BackgroundHeight));
            }
            else if (scaleMode == ScreensaverScaleMode.Fit)
            {
                g.DrawImage(
                    source,
                    FitRect(
                        source.Width,
                        source.Height,
                        BackgroundWidth,
                        BackgroundHeight));
            }
            else
            {
                Rectangle sourceRect =
                    SourceCropRect(
                        source.Width,
                        source.Height,
                        BackgroundWidth,
                        BackgroundHeight);

                g.DrawImage(
                    source,
                    new Rectangle(
                        0,
                        0,
                        BackgroundWidth,
                        BackgroundHeight),
                    sourceRect,
                    GraphicsUnit.Pixel);
            }
        }

        using Bitmap blurred =
            CreateBlurredBitmap(
                composed,
                blurPercent);

        using var output =
            new Bitmap(
                BackgroundWidth,
                BackgroundHeight,
                PixelFormat.Format24bppRgb);

        using (Graphics g = Graphics.FromImage(output))
        {
            g.Clear(Color.Black);

            float alpha =
                opacityPercent / 100f;

            using var attributes =
                new ImageAttributes();

            attributes.SetColorMatrix(
                new ColorMatrix(
                    new[]
                    {
                        new[] { 1f, 0f, 0f, 0f, 0f },
                        new[] { 0f, 1f, 0f, 0f, 0f },
                        new[] { 0f, 0f, 1f, 0f, 0f },
                        new[] { 0f, 0f, 0f, alpha, 0f },
                        new[] { 0f, 0f, 0f, 0f, 1f }
                    }));

            g.DrawImage(
                blurred,
                new Rectangle(
                    0,
                    0,
                    BackgroundWidth,
                    BackgroundHeight),
                0,
                0,
                BackgroundWidth,
                BackgroundHeight,
                GraphicsUnit.Pixel,
                attributes);
        }

        return EncodeJpegWithinLimit(
            output,
            BackgroundMaxBytes,
            [90, 84, 78, 72, 66, 60, 54, 48],
            "Main-menu background cannot be reduced below 96 KiB.");
    }

    private static GraphicsPath RoundedRectPath(
        Rectangle bounds,
        int radius)
    {
        int diameter =
            Math.Max(
                2,
                radius * 2);

        var path =
            new GraphicsPath();

        path.AddArc(
            bounds.Left,
            bounds.Top,
            diameter,
            diameter,
            180,
            90);

        path.AddArc(
            bounds.Right - diameter,
            bounds.Top,
            diameter,
            diameter,
            270,
            90);

        path.AddArc(
            bounds.Right - diameter,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);

        path.AddArc(
            bounds.Left,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            90,
            90);

        path.CloseFigure();

        return path;
    }

    private static Bitmap CreateNormalizedAppIcon(
        string path)
    {
        ValidateStaticImage(path);

        using var source =
            new Bitmap(path);

        var output =
            new Bitmap(
                IconWidth,
                IconHeight,
                PixelFormat.Format32bppArgb);

        using Graphics g =
            Graphics.FromImage(
                output);

        g.Clear(
            Color.Transparent);

        ConfigureHighQuality(g);

        Rectangle visible =
            FindVisibleBounds(
                source);

        // iOS/macOS-style normalization: every app is optically centered
        // inside the same 80x80 rounded-square envelope. We do not add a
        // black frame or force a background; transparent source artwork stays
        // transparent while oversized square icons get softly rounded corners.
        const int visualSize = 80;
        const int inset =
            (IconWidth -
             visualSize) /
            2;

        Rectangle envelope =
            new(
                inset,
                inset,
                visualSize,
                visualSize);

        Rectangle fit =
            FitRect(
                visible.Width,
                visible.Height,
                visualSize,
                visualSize);

        fit.Offset(
            inset,
            inset);

        using GraphicsPath clip =
            RoundedRectPath(
                envelope,
                18);

        GraphicsState state =
            g.Save();

        g.SetClip(
            clip);

        g.DrawImage(
            source,
            fit,
            visible,
            GraphicsUnit.Pixel);

        g.Restore(
            state);

        return output;
    }

    public static byte[] CreateIconPreviewPng(
        string path)
    {
        using Bitmap normalized =
            CreateNormalizedAppIcon(
                path);

        using var output =
            new MemoryStream();

        normalized.Save(
            output,
            ImageFormat.Png);

        return output.ToArray();
    }

    public static byte[] CreateIconAsset(string path)
    {
        using Bitmap output =
            CreateNormalizedAppIcon(
                path);

        using var stream =
            new MemoryStream(
                8 +
                IconWidth *
                IconHeight *
                2);

        using var writer =
            new BinaryWriter(stream);

        writer.Write(
            new byte[]
            {
                (byte)'P',
                (byte)'X',
                (byte)'I',
                (byte)'1'
            });

        writer.Write(
            (ushort)IconWidth);

        writer.Write(
            (ushort)IconHeight);

        for (int y = 0; y < IconHeight; y++)
        {
            for (int x = 0; x < IconWidth; x++)
            {
                Color pixel =
                    output.GetPixel(x, y);

                ushort rgb565;

                if (pixel.A <= 16)
                {
                    rgb565 =
                        IconTransparent565;
                }
                else
                {
                    int r =
                        (pixel.R * 31 + 127) /
                        255;

                    int green =
                        (pixel.G * 63 + 127) /
                        255;

                    int b =
                        (pixel.B * 31 + 127) /
                        255;

                    rgb565 =
                        (ushort)(
                            (r << 11) |
                            (green << 5) |
                            b);

                    if (rgb565 == IconTransparent565)
                        rgb565 = 0xF81E;
                }

                writer.Write(
                    rgb565);
            }
        }

        return stream.ToArray();
    }

    public static void ValidateStaticImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path))
        {
            throw new FileNotFoundException(
                "Image file was not found.",
                path);
        }

        string ext =
            Path.GetExtension(path)
                .ToLowerInvariant();

        if (ext == ".gif")
        {
            throw new InvalidOperationException(
                "Main-menu background and icons must be static images. GIF is not allowed.");
        }

        if (ext is not ".png" and
            not ".jpg" and
            not ".jpeg" and
            not ".bmp")
        {
            throw new NotSupportedException(
                "Choose a PNG, JPG/JPEG, or BMP image.");
        }
    }

    private static Rectangle SourceCropRect(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        double scale =
            Math.Max(
                targetWidth / (double)sourceWidth,
                targetHeight / (double)sourceHeight);

        int cropWidth =
            Math.Max(
                1,
                (int)Math.Round(
                    targetWidth / scale));

        int cropHeight =
            Math.Max(
                1,
                (int)Math.Round(
                    targetHeight / scale));

        return new Rectangle(
            Math.Max(
                0,
                (sourceWidth - cropWidth) / 2),
            Math.Max(
                0,
                (sourceHeight - cropHeight) / 2),
            Math.Min(
                sourceWidth,
                cropWidth),
            Math.Min(
                sourceHeight,
                cropHeight));
    }

    private static Rectangle FitRect(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        double scale =
            Math.Min(
                targetWidth / (double)sourceWidth,
                targetHeight / (double)sourceHeight);

        int width =
            Math.Max(
                1,
                (int)Math.Round(
                    sourceWidth * scale));

        int height =
            Math.Max(
                1,
                (int)Math.Round(
                    sourceHeight * scale));

        return new Rectangle(
            (targetWidth - width) / 2,
            (targetHeight - height) / 2,
            width,
            height);
    }
}
