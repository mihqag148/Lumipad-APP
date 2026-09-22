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
    public const int IconWidth = 40;
    public const int IconHeight = 40;
    public const int IconBytes = IconWidth * IconHeight * 2;

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
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;

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

        ImageCodecInfo codec =
            ImageCodecInfo.GetImageEncoders()
                .First(x => x.FormatID == ImageFormat.Jpeg.Guid);

        foreach (long quality in new long[] { 90, 84, 78, 72, 66, 60, 54, 48 })
        {
            using var stream =
                new MemoryStream();

            using var parameters =
                new EncoderParameters(1);

            parameters.Param[0] =
                new EncoderParameter(
                    Encoder.Quality,
                    quality);

            output.Save(
                stream,
                codec,
                parameters);

            if (stream.Length <= BackgroundMaxBytes)
                return stream.ToArray();
        }

        throw new InvalidOperationException(
            "Main-menu background cannot be reduced below 96 KiB.");
    }

    private static Bitmap CreateBlurredBitmap(
        Bitmap source,
        int blurPercent)
    {
        if (blurPercent <= 0)
            return new Bitmap(source);

        // Static menu art is prepared on the PC. A progressive downsample /
        // upsample gives a smooth blur without consuming any ESP32 runtime RAM.
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
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
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
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
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

    public static byte[] CreateIconRgb565(string path)
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
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;

            Rectangle fit =
                FitRect(
                    source.Width,
                    source.Height,
                    IconWidth,
                    IconHeight);

            g.DrawImage(
                source,
                fit);
        }

        var bytes =
            new byte[IconBytes];

        int offset = 0;

        for (int y = 0; y < IconHeight; y++)
        {
            for (int x = 0; x < IconWidth; x++)
            {
                Color c =
                    output.GetPixel(x, y);

                int r = c.R * c.A / 255;
                int g = c.G * c.A / 255;
                int b = c.B * c.A / 255;

                ushort rgb565 =
                    (ushort)(
                        ((r & 0xF8) << 8) |
                        ((g & 0xFC) << 3) |
                        (b >> 3));

                bytes[offset++] = (byte)(rgb565 & 0xFF);
                bytes[offset++] = (byte)(rgb565 >> 8);
            }
        }

        return bytes;
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
                (int)Math.Round(targetWidth / scale));

        int cropHeight =
            Math.Max(
                1,
                (int)Math.Round(targetHeight / scale));

        return new Rectangle(
            Math.Max(0, (sourceWidth - cropWidth) / 2),
            Math.Max(0, (sourceHeight - cropHeight) / 2),
            Math.Min(sourceWidth, cropWidth),
            Math.Min(sourceHeight, cropHeight));
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
                (int)Math.Round(sourceWidth * scale));

        int height =
            Math.Max(
                1,
                (int)Math.Round(sourceHeight * scale));

        return new Rectangle(
            (targetWidth - width) / 2,
            (targetHeight - height) / 2,
            width,
            height);
    }
}
