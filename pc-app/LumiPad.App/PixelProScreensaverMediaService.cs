using System.IO;
using System.Drawing.Imaging;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;

namespace LumiPad.App;

/// <summary>
/// PIXEL PRO-only media pipeline.
/// RYNOR ONE continues to use ScreensaverMediaService unchanged.
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
        public int StoredFps { get; init; }
        public int DurationMs { get; init; }
        public bool Optimized { get; init; }
    }

    private sealed class EncodedJpegHolder
    {
        public required byte[] Bytes { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int Quality { get; init; }
        public long SourceBytes { get; init; }
    }

    private static readonly ConditionalWeakTable<
        ScreensaverAnimation,
        EncodedGifHolder> EncodedGifs = new();

    private static readonly ConditionalWeakTable<
        ScreensaverAnimation,
        EncodedJpegHolder> EncodedJpegs = new();

    public const int PanelWidth = 480;
    public const int PanelHeight = 320;
    public const int NativePanelWidth = 320;
    public const int NativePanelHeight = 480;
    public const int MaxPlaybackFps = 60;
    public const int MinFrameIntervalMs = 17;
    public const int MaxPreviewFrames = 24;
    public const int TargetGifBytes = 1024 * 1024;
    public const int DefaultGifDurationSeconds = 15;
    public const int DefaultGifMaxFps = 60;
    public const int DefaultImageJpegQuality = 100;

    // Compatibility aliases used only for PC preview / legacy transport.
    public const int Width = PanelWidth;
    public const int Height = PanelHeight;
    public const int MaxFrames = MaxPreviewFrames;

    public static async Task<ScreensaverAnimation> LoadAsync(
        string path,
        ScreensaverScaleMode scaleMode,
        int gifMaxFps = DefaultGifMaxFps,
        int gifMaxDurationSeconds = DefaultGifDurationSeconds,
        int imageJpegQuality = DefaultImageJpegQuality)
    {
        string ext =
            Path.GetExtension(path)
                .ToLowerInvariant();

        // PIXEL PRO deliberately uses independent image/GIF rules and never
        // inherits RYNOR's Fill/Fit preference. Center means no upscale.
        ScreensaverScaleMode pixelScale =
            ScreensaverScaleMode.Center;

        return ext switch
        {
            ".gif" =>
                await Task.Run(
                    () => LoadGif(
                        path,
                        pixelScale,
                        gifMaxFps,
                        gifMaxDurationSeconds)),
            ".png" or ".jpg" or ".jpeg" or ".bmp" =>
                await Task.Run(
                    () => LoadStaticImage(
                        path,
                        pixelScale,
                        imageJpegQuality)),
            _ => throw new NotSupportedException(
                "Choose a GIF or static PNG/JPG/BMP image.")
        };
    }

    private static ScreensaverAnimation LoadStaticImage(
        string path,
        ScreensaverScaleMode scaleMode,
        int jpegQuality)
    {
        jpegQuality =
            Math.Clamp(
                jpegQuality,
                90,
                100);

        using var source =
            new Drawing.Bitmap(path);

        using Drawing.Bitmap prepared =
            PrepareStaticImage(source);

        byte[] jpeg =
            EncodeJpeg(
                prepared,
                jpegQuality);

        using Drawing.Bitmap preview =
            Resize(
                prepared,
                PanelWidth,
                PanelHeight,
                ScreensaverScaleMode.Center);

        var animation =
            new ScreensaverAnimation(
                Path.GetFileName(path),
                PanelWidth,
                PanelHeight,
                ScreensaverPixelFormat.Rgb565,
                1000,
                new[] { 1000 },
                new[] { ToRgb565Full(preview) });

        EncodedJpegs.Add(
            animation,
            new EncodedJpegHolder
            {
                Bytes = jpeg,
                Width = prepared.Width,
                Height = prepared.Height,
                Quality = jpegQuality,
                SourceBytes =
                    new FileInfo(path).Length
            });

        return animation;
    }

    private static ScreensaverAnimation LoadGif(
        string path,
        ScreensaverScaleMode scaleMode,
        int requestedMaxFps,
        int maxDurationSeconds)
    {
        requestedMaxFps =
            Math.Clamp(
                requestedMaxFps,
                20,
                60);

        maxDurationSeconds =
            Math.Clamp(
                maxDurationSeconds,
                5,
                30);

        int maxDurationMs =
            maxDurationSeconds *
            1000;

        byte[] sourceEncoded =
            File.ReadAllBytes(path);

        if (sourceEncoded.Length < 10 ||
            sourceEncoded[0] != (byte)'G' ||
            sourceEncoded[1] != (byte)'I' ||
            sourceEncoded[2] != (byte)'F')
        {
            throw new InvalidDataException(
                "The selected file is not a valid GIF.");
        }

        using var image =
            Drawing.Image.FromFile(path);

        if (image.Width <= 0 ||
            image.Height <= 0 ||
            image.Width > 1024 ||
            image.Height > 1024)
        {
            throw new NotSupportedException(
                "PIXEL PRO GIF canvas must be between 1×1 and 1024×1024.");
        }

        var dimension =
            new FrameDimension(
                image.FrameDimensionsList[0]);

        int total =
            Math.Max(
                1,
                image.GetFrameCount(dimension));

        int[] sourceDelaysMs =
            ReadGifFrameDelaysMs(
                image,
                total);

        int sourceLoopMs =
            Math.Max(
                1,
                sourceDelaysMs.Sum());

        int requestedMinDelay =
            Math.Max(
                17,
                (int)Math.Ceiling(
                    1000.0 /
                    requestedMaxFps));

        bool needsTemporalReduction =
            sourceLoopMs > maxDurationMs ||
            sourceDelaysMs.Any(
                delay =>
                    delay <
                    requestedMinDelay);

        bool overSoftTarget =
            sourceEncoded.Length >
            TargetGifBytes;

        PixelProGifOptimizationResult? optimized =
            null;

        if (needsTemporalReduction ||
            overSoftTarget)
        {
            optimized =
                PixelProGifOptimizer.Optimize(
                    path,
                    sourceDelaysMs,
                    requestedMaxFps,
                    maxDurationMs,
                    TargetGifBytes);
        }

        // 1 MiB is a quality/transfer target, not a hard rejection limit.
        // When duration/FPS must be constrained, keep the optimized result
        // even if it cannot reach 1 MiB without lowering resolution. When
        // only size triggered optimization, never replace the source with a
        // larger re-encode.
        bool useOptimized =
            optimized is not null &&
            (needsTemporalReduction ||
             optimized.Bytes.Length <
                 sourceEncoded.Length);

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

        int storedDurationMs =
            useOptimized
                ? optimized!.DurationMs
                : Math.Min(
                    sourceLoopMs,
                    maxDurationMs);

        int storedFps =
            useOptimized
                ? optimized!.StoredFps
                : Math.Clamp(
                    (int)Math.Round(
                        total * 1000.0 /
                        Math.Max(
                            1,
                            sourceLoopMs)),
                    1,
                    60);

        int previewCount =
            Math.Clamp(
                Math.Min(
                    total,
                    MaxPreviewFrames),
                1,
                MaxPreviewFrames);

        int previewLoopMs =
            Math.Max(
                1,
                Math.Min(
                    sourceLoopMs,
                    maxDurationMs));

        var sourceIndices =
            BuildPreviewIndices(
                sourceDelaysMs,
                previewCount,
                previewLoopMs);

        var previewDurations =
            BuildPreviewDurations(
                previewLoopMs,
                previewCount);

        var previewFrames =
            new List<byte[]>(
                previewCount);

        foreach (int sourceIndex in sourceIndices)
        {
            image.SelectActiveFrame(
                dimension,
                sourceIndex);

            using var bitmap =
                new Drawing.Bitmap(
                    image.Width,
                    image.Height,
                    PixelFormat.Format32bppArgb);

            using (var graphics =
                   Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(
                    Drawing.Color.Black);

                graphics.DrawImageUnscaled(
                    image,
                    0,
                    0);
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
                ScaleMode =
                    ScreensaverScaleMode.Center,
                SourceBytes =
                    sourceEncoded.LongLength,
                StoredWidth =
                    storedWidth,
                StoredHeight =
                    storedHeight,
                StoredFrames =
                    storedFrames,
                StoredFps =
                    storedFps,
                DurationMs =
                    storedDurationMs,
                Optimized =
                    useOptimized
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
        scaleMode =
            ScreensaverScaleMode.Center;
        return false;
    }

    public static bool TryGetEncodedJpeg(
        ScreensaverAnimation animation,
        out byte[] bytes,
        out int width,
        out int height)
    {
        if (EncodedJpegs.TryGetValue(
                animation,
                out EncodedJpegHolder? holder))
        {
            bytes = holder.Bytes;
            width = holder.Width;
            height = holder.Height;
            return true;
        }

        bytes = Array.Empty<byte>();
        width = 0;
        height = 0;
        return false;
    }

    public static (
        long StoredBytes,
        long SourceBytes,
        int Width,
        int Height,
        int Frames,
        int Fps,
        int DurationMs,
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
                holder.StoredFps,
                holder.DurationMs,
                holder.Optimized);
        }

        return (
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false);
    }

    public static (
        long StoredBytes,
        long SourceBytes,
        int Width,
        int Height,
        int Quality)
        GetEncodedJpegInfo(
            ScreensaverAnimation animation)
    {
        if (EncodedJpegs.TryGetValue(
                animation,
                out EncodedJpegHolder? holder))
        {
            return (
                holder.Bytes.LongLength,
                holder.SourceBytes,
                holder.Width,
                holder.Height,
                holder.Quality);
        }

        return (
            0,
            0,
            0,
            0,
            0);
    }

    private static Drawing.Bitmap PrepareStaticImage(
        Drawing.Bitmap source)
    {
        double scale =
            Math.Min(
                1.0,
                Math.Min(
                    PanelWidth /
                    (double)source.Width,
                    PanelHeight /
                    (double)source.Height));

        int width =
            Math.Max(
                1,
                (int)Math.Round(
                    source.Width *
                    scale));

        int height =
            Math.Max(
                1,
                (int)Math.Round(
                    source.Height *
                    scale));

        var prepared =
            new Drawing.Bitmap(
                width,
                height,
                PixelFormat.Format24bppRgb);

        using Drawing.Graphics g =
            Drawing.Graphics.FromImage(prepared);

        g.Clear(
            Drawing.Color.Black);
        g.InterpolationMode =
            Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode =
            Drawing2D.PixelOffsetMode.HighQuality;
        g.CompositingQuality =
            Drawing2D.CompositingQuality.HighQuality;
        g.SmoothingMode =
            Drawing2D.SmoothingMode.HighQuality;

        g.DrawImage(
            source,
            0,
            0,
            width,
            height);

        return prepared;
    }

    private static byte[] EncodeJpeg(
        Drawing.Bitmap bitmap,
        int quality)
    {
        ImageCodecInfo? codec =
            ImageCodecInfo
                .GetImageEncoders()
                .FirstOrDefault(
                    x =>
                        x.FormatID ==
                        ImageFormat.Jpeg.Guid);

        if (codec is null)
            throw new InvalidOperationException(
                "Windows JPEG encoder is unavailable.");

        using var output =
            new MemoryStream();

        using var parameters =
            new EncoderParameters(1);

        parameters.Param[0] =
            new EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality,
                (long)quality);

        bitmap.Save(
            output,
            codec,
            parameters);

        return output.ToArray();
    }

    private static IReadOnlyList<int> BuildPreviewIndices(
        IReadOnlyList<int> sourceDelaysMs,
        int count,
        int sourceLoopMs)
    {
        int total =
            sourceDelaysMs.Count;

        var cumulative =
            new int[total];

        int running = 0;

        for (int i = 0; i < total; i++)
        {
            running +=
                sourceDelaysMs[i];
            cumulative[i] = running;
        }

        var indices =
            new List<int>(
                count);

        for (int i = 0; i < count; i++)
        {
            double sourceTime =
                i *
                (sourceLoopMs /
                 (double)count);

            int sourceIndex = 0;

            while (sourceIndex < total - 1 &&
                   sourceTime >=
                   cumulative[sourceIndex])
            {
                sourceIndex++;
            }

            indices.Add(
                sourceIndex);
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
                count *
                MinFrameIntervalMs);

        int baseDelay =
            outputLoopMs /
            count;

        int remainder =
            outputLoopMs %
            count;

        var durations =
            new List<int>(
                count);

        for (int i = 0; i < count; i++)
        {
            durations.Add(
                Math.Max(
                    MinFrameIntervalMs,
                    baseDelay +
                    (i < remainder
                        ? 1
                        : 0)));
        }

        return durations;
    }

    private static int[] ReadGifFrameDelaysMs(
        Drawing.Image image,
        int frameCount)
    {
        const int PropertyTagFrameDelay =
            0x5100;

        var delays =
            Enumerable
                .Repeat(
                    100,
                    Math.Max(
                        1,
                        frameCount))
                .ToArray();

        try
        {
            var item =
                image.GetPropertyItem(
                    PropertyTagFrameDelay);

            if (item?.Value is
                { Length: >= 4 })
            {
                int entries =
                    Math.Min(
                        frameCount,
                        item.Value.Length /
                        4);

                for (int i = 0;
                     i < entries;
                     i++)
                {
                    int delayCs =
                        BitConverter.ToInt32(
                            item.Value,
                            i * 4);

                    delays[i] =
                        Math.Clamp(
                            Math.Max(
                                1,
                                delayCs) * 10,
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

    private static byte[] ToRgb565Full(
        Drawing.Bitmap source)
    {
        if (source.Width != PanelWidth ||
            source.Height != PanelHeight)
        {
            throw new InvalidOperationException(
                "PIXEL PRO image preview is not 480×320.");
        }

        var output =
            new byte[
                PanelWidth *
                PanelHeight *
                2];

        for (int y = 0;
             y < PanelHeight;
             y++)
        {
            for (int x = 0;
                 x < PanelWidth;
                 x++)
            {
                Drawing.Color p =
                    source.GetPixel(
                        x,
                        y);

                ushort rgb565 =
                    (ushort)(
                        ((p.R & 0xF8) << 8) |
                        ((p.G & 0xFC) << 3) |
                        (p.B >> 3));

                int offset =
                    (y *
                     PanelWidth +
                     x) *
                    2;

                output[offset] =
                    (byte)(
                        rgb565 &
                        0xFF);

                output[offset + 1] =
                    (byte)(
                        rgb565 >>
                        8);
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
            new byte[
                PanelWidth *
                PanelHeight];

        for (int y = 0;
             y < PanelHeight;
             y++)
        {
            for (int x = 0;
                 x < PanelWidth;
                 x++)
            {
                Drawing.Color p =
                    source.GetPixel(
                        x,
                        y);

                output[
                    y *
                    PanelWidth +
                    x] =
                    (byte)(
                        ((p.R >> 5) << 5) |
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

        using var g =
            Drawing.Graphics.FromImage(
                resized);

        g.Clear(
            Drawing.Color.Black);
        g.InterpolationMode =
            Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode =
            Drawing2D.PixelOffsetMode.HighQuality;
        g.CompositingQuality =
            Drawing2D.CompositingQuality.HighQuality;
        g.SmoothingMode =
            Drawing2D.SmoothingMode.HighQuality;

        // PIXEL PRO media never auto-upscales. Large media is reduced only
        // for display; GIF file resolution itself is preserved by the encoder.
        double scale =
            Math.Min(
                1.0,
                Math.Min(
                    width /
                    (double)source.Width,
                    height /
                    (double)source.Height));

        int drawW =
            Math.Max(
                1,
                (int)Math.Round(
                    source.Width *
                    scale));

        int drawH =
            Math.Max(
                1,
                (int)Math.Round(
                    source.Height *
                    scale));

        g.DrawImage(
            source,
            (width - drawW) / 2,
            (height - drawH) / 2,
            drawW,
            drawH);

        return resized;
    }
}
