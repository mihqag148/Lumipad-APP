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
        int brightnessPercent,
        int opacityPercent)
    {
        ValidateStaticImage(path);

        brightnessPercent =
            Math.Clamp(brightnessPercent, 20, 100);

        opacityPercent =
            Math.Clamp(opacityPercent, 0, 100);

        using var source =
            new Bitmap(path);

        using var output =
            new Bitmap(
                BackgroundWidth,
                BackgroundHeight,
                PixelFormat.Format24bppRgb);

        using (Graphics g = Graphics.FromImage(output))
        {
            g.Clear(Color.Black);
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;

            Rectangle destination =
                FillRect(
                    source.Width,
                    source.Height,
                    BackgroundWidth,
                    BackgroundHeight);

            float multiplier =
                brightnessPercent / 100f;

            float alpha =
                opacityPercent / 100f;

            using var attributes =
                new ImageAttributes();

            var matrix =
                new ColorMatrix(
                new[]
                {
                    new[] { multiplier, 0f, 0f, 0f, 0f },
                    new[] { 0f, multiplier, 0f, 0f, 0f },
                    new[] { 0f, 0f, multiplier, 0f, 0f },
                    new[] { 0f, 0f, 0f, alpha, 0f },
                    new[] { 0f, 0f, 0f, 0f, 1f }
                });

            attributes.SetColorMatrix(matrix);

            Rectangle sourceRect =
                SourceCropRect(
                    source.Width,
                    source.Height,
                    BackgroundWidth,
                    BackgroundHeight);

            g.DrawImage(
                source,
                destination,
                sourceRect.X,
                sourceRect.Y,
                sourceRect.Width,
                sourceRect.Height,
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

                // Composite transparent pixels over black, matching the panel.
                int r = c.R * c.A / 255;
                int g = c.G * c.A / 255;
                int b = c.B * c.A / 255;

                ushort rgb565 =
                    (ushort)(
                        ((r & 0xF8) << 8) |
                        ((g & 0xFC) << 3) |
                        (b >> 3));

                // ESP32 reads the file straight into uint16_t memory.
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

    private static Rectangle FillRect(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight) =>
        new(0, 0, targetWidth, targetHeight);

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
            Math.Max(1, (int)Math.Round(sourceWidth * scale));

        int height =
            Math.Max(1, (int)Math.Round(sourceHeight * scale));

        return new Rectangle(
            (targetWidth - width) / 2,
            (targetHeight - height) / 2,
            width,
            height);
    }
}
