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

    public const int IconWidth = 96;
    public const int IconHeight = 96;
    public const int IconHeaderBytes = 8;
    public const int IconPixelBytes = IconWidth * IconHeight * 2;
    public const int IconMaskBytes = (IconWidth * IconHeight + 7) / 8;
    public const int IconBytes =
        IconHeaderBytes +
        IconPixelBytes +
        IconMaskBytes;
    public const int IconMaxBytes = IconBytes;

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

    public static byte[] CreateIconBinary(string path)
    {
        using Bitmap output =
            CreateNormalizedIconBitmap(path);

        byte[] result =
            new byte[IconBytes];

        result[0] = (byte)'P';
        result[1] = (byte)'I';
        result[2] = (byte)'C';
        result[3] = (byte)'1';
        result[4] = IconWidth;
        result[5] = IconHeight;
        result[6] = 1;
        result[7] = 0;

        int pixelOffset =
            IconHeaderBytes;

        int maskOffset =
            IconHeaderBytes +
            IconPixelBytes;

        for (int y = 0; y < IconHeight; y++)
        {
            for (int x = 0; x < IconWidth; x++)
            {
                int index =
                    y * IconWidth + x;

                Color pixel =
                    output.GetPixel(x, y);

                ushort rgb565 =
                    (ushort)(
                        ((pixel.R & 0xF8) << 8) |
                        ((pixel.G & 0xFC) << 3) |
                        (pixel.B >> 3));

                int outIndex =
                    pixelOffset +
                    index * 2;

                result[outIndex] =
                    (byte)(rgb565 & 0xFF);

                result[outIndex + 1] =
                    (byte)(rgb565 >> 8);

                if (pixel.A >= 24)
                {
                    result[
                        maskOffset +
                        index / 8] |=
                            (byte)(
                                0x80 >>
                                (index & 7));
                }
            }
        }

        return result;
    }

    public static byte[] CreateIconPreviewPng(string path)
    {
        using Bitmap output =
            CreateNormalizedIconBitmap(path);

        using var stream =
            new MemoryStream();

        output.Save(
            stream,
            ImageFormat.Png);

        return stream.ToArray();
    }

    private static Bitmap CreateNormalizedIconBitmap(
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

        using (Graphics g = Graphics.FromImage(output))
        {
            g.Clear(Color.Transparent);
            ConfigureHighQuality(g);

            Rectangle visible =
                FindVisibleBounds(source);

            const int padding = 2;

            Rectangle fit =
                FitRect(
                    visible.Width,
                    visible.Height,
                    IconWidth - padding * 2,
                    IconHeight - padding * 2);

            fit.Offset(
                padding,
                padding);

            g.DrawImage(
                source,
                fit,
                visible,
                GraphicsUnit.Pixel);
        }

        return output;
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
        graphics.CompositingMode =
            CompositingMode.SourceOver;
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

        if (right < left ||
            bottom < top)
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
