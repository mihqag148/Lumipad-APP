using System.IO;
using System.Drawing.Imaging;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;

namespace LumiPad.App;

/// <summary>
/// PIXEL PRO-only media preparation for the 3.5" ILI9486 panel.
/// GIF files stay compressed but may be re-encoded for PIXEL PRO storage:
/// oversized canvases are reduced to the panel envelope, very fast animation
/// is capped at 25 FPS, and unchanged regions use delta frames. RYNOR ONE
/// continues to use ScreensaverMediaService.
/// </summary>
public static class PixelProScreensaverMediaService
{
    private sealed class EncodedGifHolder
    {
        public required byte[] Bytes { get; init; }
        public ScreensaverScaleMode ScaleMode { get; init; }
        public long SourceBytes { get; init; }
        public int StoredWidth { get; init; }
        public int StoredHeight { get; init; }
        public int StoredFrames { get; init; }
        public bool Optimized { get; init; }
    }

    private static readonly ConditionalWeakTable<
        ScreensaverAnimation,
        EncodedGifHolder> EncodedGifs = new();

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

    // Compatibility aliases used by the existing PIXEL PRO transport for
    // static/legacy frame payloads. New GIF uploads use EncodedGif instead.
    public const int Width = PanelWidth;
    public const int Height = PanelHeight;
    public const int MaxFrames = MaxPreviewFrames;

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
        byte[] sourceEncoded = File.ReadAllBytes(path);

        if (sourceEncoded.Length < 10 ||
            sourceEncoded[0] != (byte)'G' ||
            sourceEncoded[1] != (byte)'I' ||
            sourceEncoded[2] != (byte)'F')
        {
            throw new InvalidDataException(
                "The selected file is not a valid GIF.");
        }

        using var image = Drawing.Image.FromFile(path);

        if (image.Width <= 0 ||
            image.Height <= 0 ||
            image.Width > 1024 ||
            image.Height > 1024)
        {
            throw new NotSupportedException(
                "PIXEL PRO GIF canvas must be between 1×1 and 1024×1024.");
        }

        var dimension =
            new FrameDimension(image.FrameDimensionsList[0]);

        int total =
            Math.Max(1, image.GetFrameCount(dimension));

        int[] sourceDelaysMs =
            ReadGifFrameDelaysMs(image, total);

        int sourceLoopMs =
            Math.Max(1, sourceDelaysMs.Sum());

        bool needsCanvasReduction =
            image.Width > PanelWidth ||
            image.Height > PanelHeight;

        bool needsFrameRateReduction =
            sourceDelaysMs.Any(
                delay => delay < 40);

        PixelProGifOptimizationResult? optimized =
            null;

        // A normal in-range GIF is already LZW-compressed. Re-encoding every
        // selected GIF was expensive and could make large animations look as
        // if the app had hung. Only preprocess when it produces a real device
        // benefit: panel-size reduction or a >25 FPS source.
        if (needsCanvasReduction ||
            needsFrameRateReduction)
        {
            optimized =
                PixelProGifOptimizer.Optimize(
                    path,
                    sourceDelaysMs);
        }

        bool useOptimized =
            optimized is not null &&
            (needsCanvasReduction ||
             needsFrameRateReduction ||
             optimized.Bytes.LongLength * 100L <
                 sourceEncoded.LongLength * 85L);

        byte[] encoded =
            useOptimized
                ? optimized!.Bytes
                : sourceEncoded;

        int storedWidth =
            useOptimized
                ? optimized!.Width
                : image.Width;

        int storedHeight =
            useOptimized
                ? optimized!.Height
                : image.Height;

        int storedFrames =
            useOptimized
                ? optimized!.FrameCount
                : total;

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

            using Drawing.Bitmap prepared =
                Resize(
                    bitmap,
                    PanelWidth,
                    PanelHeight,
                    scaleMode);

            previewFrames.Add(
                ToRgb332Full(prepared));
        }

        int averageDelayMs =
            Math.Max(
                MinFrameIntervalMs,
                (int)Math.Round(
                    previewDurations.Average()));

        var animation =
            new ScreensaverAnimation(
                Path.GetFileName(path),
                PanelWidth,
                PanelHeight,
                ScreensaverPixelFormat.Rgb332,
                averageDelayMs,
                previewDurations,
                previewFrames);

        EncodedGifs.Add(
            animation,
            new EncodedGifHolder
            {
                Bytes = encoded,
                ScaleMode = scaleMode,
                SourceBytes = sourceEncoded.LongLength,
                StoredWidth = storedWidth,
                StoredHeight = storedHeight,
                StoredFrames = storedFrames,
                Optimized = useOptimized
            });

        return animation;
    }

    public static bool TryGetEncodedGif(
        ScreensaverAnimation animation,
        out byte[] bytes,
        out ScreensaverScaleMode scaleMode)
    {
        if (EncodedGifs.TryGetValue(
                animation,
                out EncodedGifHolder? holder))
        {
            bytes = holder.Bytes;
            scaleMode = holder.ScaleMode;
            return true;
        }

        bytes = Array.Empty<byte>();
        scaleMode = ScreensaverScaleMode.Fill;
        return false;
    }

    public static long GetEncodedGifSize(
        ScreensaverAnimation animation) =>
        EncodedGifs.TryGetValue(
            animation,
            out EncodedGifHolder? holder)
            ? holder.Bytes.LongLength
            : 0;

    public static (
        long StoredBytes,
        long SourceBytes,
        int Width,
        int Height,
        int Frames,
        bool Optimized)
        GetEncodedGifInfo(
            ScreensaverAnimation animation)
    {
        if (EncodedGifs.TryGetValue(
                animation,
                out EncodedGifHolder? holder))
        {
            return (
                holder.Bytes.LongLength,
                holder.SourceBytes,
                holder.StoredWidth,
                holder.StoredHeight,
                holder.StoredFrames,
                holder.Optimized);
        }

        return (
            0,
            0,
            0,
            0,
            0,
            false);
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
                // PIXEL PRO "Center" means original size when possible:
                // never upscale small media; only shrink if it is larger
                // than the 480x320 panel, while preserving aspect ratio.
                double scale =
                    Math.Min(
                        1.0,
                        Math.Min(
                            width / (double)source.Width,
                            height / (double)source.Height));

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
