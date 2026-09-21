using System.IO;
using System.Drawing.Imaging;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace LumiPad.App;

/// <summary>
/// PIXEL PRO-only media converter for the 3.5" ILI9486 landscape panel.
/// RYNOR ONE continues to use ScreensaverMediaService unchanged.
/// </summary>
public static class PixelProScreensaverMediaService
{
    public const int PanelWidth = 480;
    public const int PanelHeight = 320;

    // Animated media is stored at exact half resolution and expanded 2x
    // in firmware. This keeps the 3:2 aspect ratio while fitting a smooth
    // loop into the LOLIN S2 Mini's 2 MB PSRAM.
    public const int Width = 240;
    public const int Height = 160;

    public const int MaxFrames = 32;
    public const int MaxPlaybackFps = 60;

    // Integer millisecond scheduling cannot represent 16.666... ms exactly.
    // 17 ms guarantees that the animation never exceeds the 60 FPS cap.
    public const int MinFrameIntervalMs = 17;

    public static async Task<ScreensaverAnimation> LoadAsync(
        string path,
        ScreensaverScaleMode scaleMode)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
        {
            ".gif" => await Task.Run(() => LoadGif(path, scaleMode)),
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

    private static ScreensaverAnimation LoadGif(
        string path,
        ScreensaverScaleMode scaleMode)
    {
        using var image = Drawing.Image.FromFile(path);
        var dimension =
            new FrameDimension(image.FrameDimensionsList[0]);

        int total = Math.Max(1, image.GetFrameCount(dimension));
        int[] sourceDelaysMs = ReadGifFrameDelaysMs(image, total);
        int sourceLoopMs = Math.Max(1, sourceDelaysMs.Sum());

        bool exactTimingFits =
            total <= MaxFrames &&
            sourceDelaysMs.All(delay => delay >= MinFrameIntervalMs);

        int maxFramesByRate =
            Math.Max(
                1,
                (int)Math.Floor(
                    sourceLoopMs /
                    (1000.0 / MaxPlaybackFps)));

        int count = exactTimingFits
            ? total
            : Math.Min(
                Math.Min(total, MaxFrames),
                maxFramesByRate);

        count = Math.Max(1, count);

        var frameDurations = new List<int>(count);
        var sourceIndices = new List<int>(count);

        if (exactTimingFits)
        {
            for (int i = 0; i < total; i++)
            {
                sourceIndices.Add(i);
                frameDurations.Add(
                    Math.Max(
                        MinFrameIntervalMs,
                        sourceDelaysMs[i]));
            }
        }
        else
        {
            int outputLoopMs =
                Math.Max(
                    sourceLoopMs,
                    count * MinFrameIntervalMs);

            int baseDelay = outputLoopMs / count;
            int remainder = outputLoopMs % count;

            var cumulative = new int[total];
            int running = 0;

            for (int i = 0; i < total; i++)
            {
                running += sourceDelaysMs[i];
                cumulative[i] = running;
            }

            for (int i = 0; i < count; i++)
            {
                frameDurations.Add(
                    Math.Max(
                        MinFrameIntervalMs,
                        baseDelay + (i < remainder ? 1 : 0)));

                double sourceTime =
                    i * (sourceLoopMs / (double)count);

                int srcIndex = 0;
                while (srcIndex < total - 1 &&
                       sourceTime >= cumulative[srcIndex])
                {
                    srcIndex++;
                }

                sourceIndices.Add(srcIndex);
            }
        }

        var frames = new List<byte[]>(count);

        foreach (int srcIndex in sourceIndices)
        {
            image.SelectActiveFrame(dimension, srcIndex);

            using var bitmap = new Drawing.Bitmap(
                image.Width,
                image.Height,
                PixelFormat.Format32bppArgb);

            using (var graphics =
                   Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(Drawing.Color.Transparent);
                graphics.DrawImageUnscaled(image, 0, 0);
            }

            frames.Add(ToRgb332(bitmap, scaleMode));
        }

        int averageDelayMs =
            Math.Max(
                MinFrameIntervalMs,
                (int)Math.Round(frameDurations.Average()));

        return new ScreensaverAnimation(
            Path.GetFileName(path),
            Width,
            Height,
            ScreensaverPixelFormat.Rgb332,
            averageDelayMs,
            frameDurations,
            frames);
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
            var item = image.GetPropertyItem(PropertyTagFrameDelay);

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

    private static byte[] ToRgb332(
        Drawing.Bitmap source,
        ScreensaverScaleMode scaleMode)
    {
        using var resized =
            Resize(
                source,
                Width,
                Height,
                scaleMode);

        var output = new byte[Width * Height];

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                Drawing.Color p = resized.GetPixel(x, y);

                output[y * Width + x] =
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
