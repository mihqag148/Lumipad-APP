using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using Drawing = System.Drawing;

namespace LumiPad.App;

/// <summary>
/// PIXEL PRO-only media pipeline.
/// RYNOR ONE continues to use ScreensaverMediaService unchanged.
/// </summary>
public static class PixelProScreensaverMediaService
{
    private sealed class PackedAnimationHolder
    {
        public required byte[] Bytes { get; init; }
        public ScreensaverScaleMode ScaleMode { get; init; }
        public long SourceBytes { get; init; }
        public int StorageWidth { get; init; }
        public int StorageHeight { get; init; }
        public int Frames { get; init; }
        public int Fps { get; init; }
        public int DurationMs { get; init; }
        public PixelProPackedColorMode ColorMode { get; init; }
        public bool EmergencyFps { get; init; }
    }

    private sealed class EncodedJpegHolder
    {
        public required byte[] Bytes { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int Quality { get; init; }
        public long SourceBytes { get; init; }
        public ScreensaverScaleMode ScaleMode { get; init; }
    }

    private static readonly ConditionalWeakTable<
        ScreensaverAnimation,
        PackedAnimationHolder> PackedAnimations = new();

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
    public const int TargetGifBytes =
        PixelProPackedAnimationEncoder.HardTargetBytes;
    public const int DefaultGifDurationSeconds = 10;
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

        ScreensaverScaleMode pixelScale =
            NormalizePixelScale(
                scaleMode);

        return ext switch
        {
            ".gif" =>
                await RunCpuBoundLowPriorityAsync(
                    () => LoadGif(
                        path,
                        pixelScale,
                        gifMaxDurationSeconds)),

            ".png" or ".jpg" or ".jpeg" or ".bmp" =>
                await RunCpuBoundLowPriorityAsync(
                    () => LoadStaticImage(
                        path,
                        pixelScale,
                        imageJpegQuality)),

            _ =>
                throw new NotSupportedException(
                    "Choose a GIF or static PNG/JPG/BMP image.")
        };
    }

    private static Task<T> RunCpuBoundLowPriorityAsync<T>(
        Func<T> work) =>
        Task.Factory.StartNew(
            () =>
            {
                Thread thread =
                    Thread.CurrentThread;

                ThreadPriority oldPriority =
                    thread.Priority;

                try
                {
                    thread.Priority =
                        ThreadPriority.BelowNormal;

                    return work();
                }
                finally
                {
                    thread.Priority =
                        oldPriority;
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    public static ScreensaverScaleMode NormalizePixelScale(
        ScreensaverScaleMode scaleMode) =>
        scaleMode switch
        {
            ScreensaverScaleMode.Fill => ScreensaverScaleMode.Fill,
            ScreensaverScaleMode.Fit => ScreensaverScaleMode.Fit,
            ScreensaverScaleMode.Stretch => ScreensaverScaleMode.Stretch,
            ScreensaverScaleMode.Tile => ScreensaverScaleMode.Tile,
            ScreensaverScaleMode.Center => ScreensaverScaleMode.Center,
            ScreensaverScaleMode.Span => ScreensaverScaleMode.Span,
            _ => ScreensaverScaleMode.Fill,
        };

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

        using Drawing.Bitmap logical =
            PixelProPackedAnimationEncoder.RenderStaticLogical(
                source,
                scaleMode);

        byte[] jpeg =
            EncodeJpeg(
                logical,
                jpegQuality);

        var animation =
            new ScreensaverAnimation(
                Path.GetFileName(path),
                PanelWidth,
                PanelHeight,
                ScreensaverPixelFormat.Rgb565,
                1000,
                new[] { 1000 },
                new[]
                {
                    ToRgb565Full(
                        logical)
                });

        EncodedJpegs.Add(
            animation,
            new EncodedJpegHolder
            {
                Bytes = jpeg,
                Width = PanelWidth,
                Height = PanelHeight,
                Quality = jpegQuality,
                SourceBytes =
                    new FileInfo(path).Length,
                ScaleMode =
                    scaleMode
            });

        return animation;
    }

    private static ScreensaverAnimation LoadGif(
        string path,
        ScreensaverScaleMode scaleMode,
        int maxDurationSeconds)
    {
        byte[] signature =
            File.ReadAllBytes(path)
                .Take(10)
                .ToArray();

        if (signature.Length < 10 ||
            signature[0] != (byte)'G' ||
            signature[1] != (byte)'I' ||
            signature[2] != (byte)'F')
        {
            throw new InvalidDataException(
                "The selected file is not a valid GIF.");
        }

        PixelProPackedAnimationResult packed =
            PixelProPackedAnimationEncoder.EncodeBest(
                path,
                scaleMode,
                maxDurationSeconds);

        using Drawing.Image image =
            Drawing.Image.FromFile(path);

        var dimension =
            new FrameDimension(
                image.FrameDimensionsList[0]);

        int totalFrames =
            Math.Max(
                1,
                image.GetFrameCount(
                    dimension));

        int[] sourceDelays =
            ReadGifFrameDelaysMs(
                image,
                totalFrames);

        int sourceLoopMs =
            Math.Max(
                1,
                sourceDelays.Sum());

        int previewDurationMs =
            Math.Min(
                packed.DurationMs,
                sourceLoopMs);

        int previewCount =
            Math.Clamp(
                Math.Min(
                    totalFrames,
                    MaxPreviewFrames),
                1,
                MaxPreviewFrames);

        IReadOnlyList<int> sourceIndices =
            BuildPreviewIndices(
                sourceDelays,
                previewCount,
                previewDurationMs);

        IReadOnlyList<int> previewDurations =
            BuildPreviewDurations(
                previewDurationMs,
                previewCount);

        var previewFrames =
            new List<byte[]>(
                previewCount);

        foreach (int sourceIndex in sourceIndices)
        {
            image.SelectActiveFrame(
                dimension,
                sourceIndex);

            using var selected =
                new Drawing.Bitmap(
                    image.Width,
                    image.Height,
                    PixelFormat.Format24bppRgb);

            using (Drawing.Graphics graphics =
                   Drawing.Graphics.FromImage(
                       selected))
            {
                graphics.Clear(
                    Drawing.Color.Black);

                graphics.DrawImageUnscaled(
                    image,
                    0,
                    0);
            }

            using Drawing.Bitmap logical =
                PixelProPackedAnimationEncoder.RenderStaticLogical(
                    selected,
                    scaleMode);

            previewFrames.Add(
                ToRgb332Full(
                    logical));
        }

        int averageDelay =
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
                averageDelay,
                previewDurations,
                previewFrames);

        PackedAnimations.Add(
            animation,
            new PackedAnimationHolder
            {
                Bytes =
                    packed.Bytes,
                ScaleMode =
                    packed.ScaleMode,
                SourceBytes =
                    packed.SourceBytes,
                StorageWidth =
                    packed.StorageWidth,
                StorageHeight =
                    packed.StorageHeight,
                Frames =
                    packed.FrameCount,
                Fps =
                    packed.Fps,
                DurationMs =
                    packed.DurationMs,
                ColorMode =
                    packed.ColorMode,
                EmergencyFps =
                    packed.UsedEmergencyFps
            });

        return animation;
    }

    public static bool TryGetPackedAnimation(
        ScreensaverAnimation animation,
        out byte[] bytes)
    {
        if (PackedAnimations.TryGetValue(
                animation,
                out PackedAnimationHolder? holder))
        {
            bytes =
                holder.Bytes;
            return true;
        }

        bytes =
            Array.Empty<byte>();
        return false;
    }

    // Kept for old callers / older PIXEL firmware fallback. New app builds
    // send GIF animations through the packed PXQ path instead.
    public static bool TryGetEncodedGif(
        ScreensaverAnimation animation,
        out byte[] bytes,
        out ScreensaverScaleMode scaleMode)
    {
        bytes =
            Array.Empty<byte>();

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
            bytes =
                holder.Bytes;

            width =
                holder.Width;

            height =
                holder.Height;

            return true;
        }

        bytes =
            Array.Empty<byte>();

        width =
            0;

        height =
            0;

        return false;
    }

    public static (
        long StoredBytes,
        long SourceBytes,
        int StorageWidth,
        int StorageHeight,
        int Frames,
        int Fps,
        int DurationMs,
        string ColorMode,
        string ScaleMode,
        bool EmergencyFps)
        GetPackedAnimationInfo(
            ScreensaverAnimation animation)
    {
        if (PackedAnimations.TryGetValue(
                animation,
                out PackedAnimationHolder? holder))
        {
            return (
                holder.Bytes.LongLength,
                holder.SourceBytes,
                holder.StorageWidth,
                holder.StorageHeight,
                holder.Frames,
                holder.Fps,
                holder.DurationMs,
                ColorModeLabel(
                    holder.ColorMode),
                holder.ScaleMode.ToString(),
                holder.EmergencyFps);
        }

        return (
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "",
            "",
            false);
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
        var info =
            GetPackedAnimationInfo(
                animation);

        return (
            info.StoredBytes,
            info.SourceBytes,
            info.StorageWidth,
            info.StorageHeight,
            info.Frames,
            info.Fps,
            info.DurationMs,
            info.StoredBytes > 0);
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

    private static string ColorModeLabel(
        PixelProPackedColorMode mode) =>
        mode switch
        {
            PixelProPackedColorMode.Rgb888 =>
                "RGB888",

            PixelProPackedColorMode.Rgb565 =>
                "RGB565",

            PixelProPackedColorMode.Palette256 =>
                "Palette 256",

            PixelProPackedColorMode.Palette16 =>
                "Palette 16",

            PixelProPackedColorMode.Palette4 =>
                "Palette 4",

            PixelProPackedColorMode.Palette2 =>
                "Palette 2",

            _ =>
                mode.ToString()
        };

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
        {
            throw new InvalidOperationException(
                "Windows JPEG encoder is unavailable.");
        }

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
            PropertyItem item =
                image.GetPropertyItem(
                    PropertyTagFrameDelay);

            if (item.Value is
                { Length: >= 4 })
            {
                int count =
                    Math.Min(
                        frameCount,
                        item.Value.Length /
                        4);

                for (int i = 0;
                     i < count;
                     i++)
                {
                    int delayCs =
                        BitConverter.ToInt32(
                            item.Value,
                            i *
                            4);

                    delays[i] =
                        Math.Clamp(
                            Math.Max(
                                1,
                                delayCs) *
                            10,
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

    private static IReadOnlyList<int> BuildPreviewIndices(
        IReadOnlyList<int> sourceDelaysMs,
        int count,
        int sourceLoopMs)
    {
        int total =
            sourceDelaysMs.Count;

        var cumulative =
            new int[total];

        int running =
            0;

        for (int i = 0;
             i < total;
             i++)
        {
            running +=
                sourceDelaysMs[i];

            cumulative[i] =
                running;
        }

        var indices =
            new List<int>(
                count);

        for (int i = 0;
             i < count;
             i++)
        {
            double sourceTime =
                i *
                (sourceLoopMs /
                 (double)count);

            int sourceIndex =
                0;

            while (sourceIndex <
                       total - 1 &&
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
        int totalMs =
            Math.Max(
                count *
                MinFrameIntervalMs,
                sourceLoopMs);

        int baseDelay =
            totalMs /
            count;

        int remainder =
            totalMs %
            count;

        var durations =
            new List<int>(
                count);

        for (int i = 0;
             i < count;
             i++)
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
}
