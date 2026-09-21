using System.IO;
using System.Drawing.Imaging;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace LumiPad.App;

/// <summary>
/// PIXEL PRO-only media preparation for the 3.5" ILI9486 panel.
/// GIF files stay compressed and are sent to PIXEL PRO as the original
/// full-resolution GIF. RYNOR ONE continues to use ScreensaverMediaService.
/// </summary>
public static class PixelProScreensaverMediaService
{
    public const int PanelWidth = 480;
    public const int PanelHeight = 320;
    public const int NativePanelWidth = 320;
    public const int NativePanelHeight = 480;

    // Device playback is capped at 60 FPS. The GIF itself is not converted
    // to RGB frame blobs; firmware decodes the original LZW-compressed file.
    public const int MaxPlaybackFps = 60;
    public const int MinFrameIntervalMs = 17;

    // Preview frames only exist on the PC. They are never uploaded.
    public const int MaxPreviewFrames = 24;

    public static async Task<ScreensaverAnimation> LoadAsync(
        string path,
        ScreensaverScaleMode scaleMode)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
        {
            ".gif" => await Task.Run(() => LoadGif(path)),
            ".png" or ".jpg" or ".jpeg" or ".bmp" =>
                await Task.Run(() => LoadStaticImage(path, scaleMode)),
            _ => throw new NotSupportedException(
                "Choose a GIF or static PNG/JPG/BMP image.")
        };
    }

    private static ScreensaverAnimation LoadStaticImage(
        string path,
        ScreensaverScaleMode scaleMode)
    {
        using var bitmap = new Drawing.Bitmap(path);

        return new ScreensaverAnimation(
            Path.GetFileName(path),
            PanelWidth,
            PanelHeight,
            ScreensaverPixelFormat.Rgb565,
            1000,
            new[] { 1000 },
            new[] { ToRgb565(bitmap, scaleMode) });
    }

    private static ScreensaverAnimation LoadGif(string path)
    {
        byte[] encoded = File.ReadAllBytes(path);

        if (encoded.Length < 10 ||
            encoded[0] != (byte)'G' ||
            encoded[1] != (byte)'I' ||
            encoded[2] != (byte)'F')
        {
            throw new InvalidDataException("The selected file is not a valid GIF.");
        }

        using var image = Drawing.Image.FromFile(path);

        bool landscape =
            image.Width == PanelWidth &&
            image.Height == PanelHeight;

        bool nativePortrait =
            image.Width == NativePanelWidth &&
            image.Height == NativePanelHeight;

        if (!landscape && !nativePortrait)
        {
            throw new NotSupportedException(
                "PIXEL PRO GIF must be full panel resolution: " +
                "480×320 landscape or native 320×480. " +
                "GIF is sent directly without 2× scaling.");
        }

        var dimension =
            new FrameDimension(image.FrameDimensionsList[0]);

        int total =
            Math.Max(1, image.GetFrameCount(dimension));

        int[] sourceDelaysMs =
            ReadGifFrameDelaysMs(image, total);

        int sourceLoopMs =
            Math.Max(1, sourceDelaysMs.Sum());

        int previewCount =
            Math.Clamp(
                Math.Min(total, MaxPreviewFrames),
                1,
                MaxPreviewFrames);

        var sourceIndices =
            BuildPreviewIndices(
                sourceDelaysMs,
                previewCount,
                sourceLoopMs);

        var previewDurations =
            BuildPreviewDurations(
                sourceLoopMs,
                previewCount);

        var previewFrames =
            new List<byte[]>(previewCount);

        foreach (int sourceIndex in sourceIndices)
        {
            image.SelectActiveFrame(dimension, sourceIndex);

            using var bitmap = new Drawing.Bitmap(
                image.Width,
                image.Height,
                PixelFormat.Format32bppArgb);

            using (var graphics =
                   Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(Drawing.Color.Black);
                graphics.DrawImageUnscaled(image, 0, 0);
            }

            if (nativePortrait)
            {
                bitmap.RotateFlip(
                    Drawing.RotateFlipType.Rotate90FlipNone);
            }

            previewFrames.Add(ToRgb332Full(bitmap));
        }

        int averageDelayMs =
            Math.Max(
                MinFrameIntervalMs,
                (int)Math.Round(
                    previewDurations.Average()));

        return new ScreensaverAnimation(
            Path.GetFileName(path),
            PanelWidth,
            PanelHeight,
            ScreensaverPixelFormat.Rgb332,
            averageDelayMs,
            previewDurations,
            previewFrames)
        {
            EncodedGif = encoded
        };
    }

    private static IReadOnlyList<int> BuildPreviewIndices(
        IReadOnlyList<int> sourceDelaysMs,
        int count,
        int sourceLoopMs)
    {
        int total = sourceDelaysMs.Count;
        var cumulative = new int[total];
        int running = 0;

        for (int i = 0; i < total; i++)
        {
            running += sourceDelaysMs[i];
            cumulative[i] = running;
        }

        var indices = new List<int>(count);

        for (int i = 0; i < count; i++)
        {
            double sourceTime =
                i * (sourceLoopMs / (double)count);

            int sourceIndex = 0;

            while (sourceIndex < total - 1 &&
                   sourceTime >= cumulative[sourceIndex])
            {
                sourceIndex++;
            }

            indices.Add(sourceIndex);
        }

        return indices;
    }

    private static IReadOnlyList<int> BuildPreviewDurations(
        int sourceLoopMs,
        int count)
    {
        int outputLoopMs =
            Math.Max(
                sourceLoopMs,
                count * MinFrameIntervalMs);

        int baseDelay = outputLoopMs / count;
        int remainder = outputLoopMs % count;

        var durations = new List<int>(count);

        for (int i = 0; i < count; i++)
        {
            durations.Add(
                Math.Max(
                    MinFrameIntervalMs,
                    baseDelay + (i < remainder ? 1 : 0)));
        }

        return durations;
    }

    private static int[] ReadGifFrameDelaysMs(
        Drawing.Image image,
        int frameCount)
    {
        const int PropertyTagFrameDelay = 0x5100;

        var delays =
            Enumerable
                .Repeat(100, Math.Max(1, frameCount))
                .ToArray();

        try
        {
            var item =
                image.GetPropertyItem(PropertyTagFrameDelay);

            if (item?.Value is { Length: >= 4 })
            {
                int entries =
                    Math.Min(
                        frameCount,
                        item.Value.Length / 4);

                for (int i = 0; i < entries; i++)
                {
                    int delayCs =
                        BitConverter.ToInt32(
                            item.Value,
                            i * 4);

                    delays[i] =
                        Math.Clamp(
                            Math.Max(1, delayCs) * 10,
                            10,
                            5000);
                }
            }
        }
        catch
        {
        }

        return delays;
    }

    private static byte[] ToRgb565(
        Drawing.Bitmap source,
        ScreensaverScaleMode scaleMode)
    {
        using var resized =
            Resize(
                source,
                PanelWidth,
                PanelHeight,
                scaleMode);

        var output =
            new byte[PanelWidth * PanelHeight * 2];

        for (int y = 0; y < PanelHeight; y++)
        {
            for (int x = 0; x < PanelWidth; x++)
            {
                Drawing.Color p = resized.GetPixel(x, y);

                ushort rgb565 = (ushort)(
                    ((p.R & 0xF8) << 8) |
                    ((p.G & 0xFC) << 3) |
                    (p.B >> 3));

                int offset =
                    (y * PanelWidth + x) * 2;

                output[offset] =
                    (byte)(rgb565 & 0xFF);

                output[offset + 1] =
                    (byte)(rgb565 >> 8);
            }
        }

        return output;
    }

    private static byte[] ToRgb332Full(
        Drawing.Bitmap source)
    {
        if (source.Width != PanelWidth ||
            source.Height != PanelHeight)
        {
            throw new InvalidOperationException(
                "PIXEL PRO GIF preview is not 480×320.");
        }

        var output =
            new byte[PanelWidth * PanelHeight];

        for (int y = 0; y < PanelHeight; y++)
        {
            for (int x = 0; x < PanelWidth; x++)
            {
                Drawing.Color p = source.GetPixel(x, y);

                output[y * PanelWidth + x] =
                    (byte)(((p.R >> 5) << 5) |
                           ((p.G >> 5) << 2) |
                           (p.B >> 6));
            }
        }

        return output;
    }

    private static Drawing.Bitmap Resize(
        Drawing.Bitmap source,
        int width,
        int height,
        ScreensaverScaleMode scaleMode)
    {
        var resized =
            new Drawing.Bitmap(
                width,
                height,
                PixelFormat.Format24bppRgb);

        using var g = Drawing.Graphics.FromImage(resized);

        g.Clear(Drawing.Color.Black);
        g.InterpolationMode =
            Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode =
            Drawing2D.PixelOffsetMode.HighQuality;
        g.CompositingQuality =
            Drawing2D.CompositingQuality.HighQuality;
        g.SmoothingMode =
            Drawing2D.SmoothingMode.HighQuality;

        switch (scaleMode)
        {
            case ScreensaverScaleMode.Stretch:
                g.DrawImage(source, 0, 0, width, height);
                break;

            case ScreensaverScaleMode.Fit:
            {
                double scale =
                    Math.Min(
                        width / (double)source.Width,
                        height / (double)source.Height);

                int drawW =
                    Math.Max(
                        1,
                        (int)Math.Round(
                            source.Width * scale));

                int drawH =
                    Math.Max(
                        1,
                        (int)Math.Round(
                            source.Height * scale));

                g.DrawImage(
                    source,
                    (width - drawW) / 2,
                    (height - drawH) / 2,
                    drawW,
                    drawH);
                break;
            }

            case ScreensaverScaleMode.Center:
            {
                int drawW =
                    Math.Min(source.Width, width);

                int drawH =
                    Math.Min(source.Height, height);

                int sx =
                    Math.Max(
                        0,
                        (source.Width - drawW) / 2);

                int sy =
                    Math.Max(
                        0,
                        (source.Height - drawH) / 2);

                int dx = (width - drawW) / 2;
                int dy = (height - drawH) / 2;

                g.DrawImage(
                    source,
                    new Drawing.Rectangle(
                        dx,
                        dy,
                        drawW,
                        drawH),
                    new Drawing.Rectangle(
                        sx,
                        sy,
                        drawW,
                        drawH),
                    Drawing.GraphicsUnit.Pixel);
                break;
            }

            case ScreensaverScaleMode.Tile:
            {
                double scale =
                    Math.Min(
                        0.5,
                        Math.Min(
                            width /
                            (double)source.Width,
                            height /
                            (double)source.Height));

                int tileW =
                    Math.Max(
                        8,
                        (int)Math.Round(
                            source.Width * scale));

                int tileH =
                    Math.Max(
                        8,
                        (int)Math.Round(
                            source.Height * scale));

                using var tile =
                    new Drawing.Bitmap(
                        tileW,
                        tileH);

                using (var tg =
                       Drawing.Graphics.FromImage(tile))
                {
                    tg.InterpolationMode =
                        Drawing2D.InterpolationMode.HighQualityBilinear;

                    tg.DrawImage(
                        source,
                        0,
                        0,
                        tileW,
                        tileH);
                }

                using var brush =
                    new Drawing.TextureBrush(
                        tile,
                        Drawing2D.WrapMode.Tile);

                g.FillRectangle(
                    brush,
                    0,
                    0,
                    width,
                    height);
                break;
            }

            case ScreensaverScaleMode.Span:
            case ScreensaverScaleMode.Fill:
            default:
            {
                double scale =
                    Math.Max(
                        width / (double)source.Width,
                        height / (double)source.Height);

                if (scaleMode ==
                    ScreensaverScaleMode.Span)
                {
                    scale *= 1.08;
                }

                int drawW =
                    Math.Max(
                        1,
                        (int)Math.Ceiling(
                            source.Width * scale));

                int drawH =
                    Math.Max(
                        1,
                        (int)Math.Ceiling(
                            source.Height * scale));

                g.DrawImage(
                    source,
                    (width - drawW) / 2,
                    (height - drawH) / 2,
                    drawW,
                    drawH);
                break;
            }
        }

        return resized;
    }
}
