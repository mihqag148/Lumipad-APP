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

    public static byte[] CreateIconAsset(string path)
    {
        ValidateStaticImage(path);

        using var source =
            new Bitmap(path);

        using var output =
            new Bitmap(
                IconWidth,
                IconHeight,
                PixelFormat.Format32bppArgb);

        using (Graphics g = Graphics.FromImage(output))
        {
            g.Clear(Color.Transparent);
            ConfigureHighQuality(g);

            Rectangle visible =
                FindVisibleBounds(source);

            const int padding = 2;
            int targetWidth =
                IconWidth - padding * 2;
            int targetHeight =
                IconHeight - padding * 2;

            Rectangle fit =
                FitRect(
                    visible.Width,
                    visible.Height,
                    targetWidth,
                    targetHeight);

            fit.Offset(
                padding,
                padding);

            g.DrawImage(
                source,
                fit,
                visible,
                GraphicsUnit.Pixel);
        }

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

                    // Reserve magenta as the transparent key. Move a genuine
                    // source pixel by one blue step if it quantizes to it.
                    if (rgb565 == IconTransparent565)
                        rgb565 = 0xF81E;
                }

                writer.Write(rgb565);
            }
        }

        writer.Flush();

        byte[] payload =
            stream.ToArray();

        if (payload.Length > IconMaxBytes)
        {
            throw new InvalidOperationException(
                "Main-menu icon exceeds the 24 KiB icon budget.");
        }

        return payload;
    }

    private static byte[] EncodeJpegWithinLimit(
        Bitmap image,
        int maxBytes,
        long[] qualities,
        string failureMessage)
    {
        ImageCodecInfo codec =
            ImageCodecInfo.GetImageEncoders()
                .First(
                    x =>
                        x.FormatID ==
                        ImageFormat.Jpeg.Guid);

        foreach (long quality in qualities)
        {
            using var stream =
                new MemoryStream();

            using var parameters =
                new EncoderParameters(1);

            parameters.Param[0] =
                new EncoderParameter(
                    Encoder.Quality,
                    quality);

            image.Save(
                stream,
                codec,
                parameters);

            if (stream.Length <= maxBytes)
                return stream.ToArray();
        }

        throw new InvalidOperationException(
            failureMessage);
    }

    private static Bitmap CreateBlurredBitmap(
        Bitmap source,
        int blurPercent)
    {
        if (blurPercent <= 0)
            return new Bitmap(source);

        double amount =
            Math.Clamp(
                blurPercent / 100.0,
                0.0,
                1.0);

        int divisor =
            2 +
            (int)Math.Round(
                amount * 14.0);

        int smallWidth =
            Math.Max(
                1,
                source.Width / divisor);

        int smallHeight =
            Math.Max(
                1,
                source.Height / divisor);

        using var small =
            new Bitmap(
                smallWidth,
                smallHeight,
                PixelFormat.Format24bppRgb);

        using (Graphics g = Graphics.FromImage(small))
        {
            g.Clear(Color.Black);
            g.CompositingQuality =
                CompositingQuality.HighQuality;
            g.InterpolationMode =
                InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode =
                PixelOffsetMode.HighQuality;

            g.DrawImage(
                source,
                new Rectangle(
                    0,
                    0,
                    smallWidth,
                    smallHeight));
        }

        var result =
            new Bitmap(
                source.Width,
                source.Height,
                PixelFormat.Format24bppRgb);

        using (Graphics g = Graphics.FromImage(result))
        {
            g.Clear(Color.Black);
            ConfigureHighQuality(g);
            g.DrawImage(
                small,
                new Rectangle(
                    0,
                    0,
                    source.Width,
                    source.Height));
        }

        return result;
    }

    private static void ConfigureHighQuality(
        Graphics graphics)
    {
        graphics.CompositingQuality =
            CompositingQuality.HighQuality;
        graphics.InterpolationMode =
            InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode =
            PixelOffsetMode.HighQuality;
        graphics.SmoothingMode =
            SmoothingMode.HighQuality;
    }

    private static Rectangle FindVisibleBounds(
        Bitmap source)
    {
        bool hasAlpha =
            Image.IsAlphaPixelFormat(
                source.PixelFormat);

        if (!hasAlpha)
        {
            return new Rectangle(
                0,
                0,
                source.Width,
                source.Height);
        }

        int left = source.Width;
        int top = source.Height;
        int right = -1;
        int bottom = -1;

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                if (source.GetPixel(x, y).A <= 8)
                    continue;

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top)
        {
            return new Rectangle(
                0,
                0,
                source.Width,
                source.Height);
        }

        return Rectangle.FromLTRB(
            left,
            top,
            right + 1,
            bottom + 1);
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
