using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace LumiPad.App;

internal enum PixelProPackedColorMode : byte
{
    Rgb888 = 0,
    Rgb565 = 1,
    Palette256 = 2,
    Palette16 = 3,
    Palette4 = 4,
    Palette2 = 5,
}

internal sealed record PixelProPackedAnimationResult(
    byte[] Bytes,
    int StorageWidth,
    int StorageHeight,
    int FrameCount,
    int Fps,
    int DurationMs,
    PixelProPackedColorMode ColorMode,
    ScreensaverScaleMode ScaleMode,
    long SourceBytes,
    bool UsedEmergencyFps);

/// <summary>
/// QMK Quantum Painter-inspired PIXEL PRO animation packer.
///
/// The wire format is PIXEL-specific rather than QGF, but follows the same
/// useful ideas for a small MCU: delta frames, per-run RLE, and selectable
/// native/palette color depths. A progressive Smart Delta pass suppresses
/// visually insignificant temporal noise before reducing resolution/FPS.
/// New PIXEL PRO media keeps a quality floor of 360×240 and RGB565 while
/// using up to 2 MiB when needed.
/// </summary>
internal static class PixelProPackedAnimationEncoder
{
    public const int DisplayWidth = 480;
    public const int DisplayHeight = 320;
    public const int PreferredMinBytes = 800 * 1024;
    public const int HardTargetBytes = 2 * 1024 * 1024;

    private const int MaxCanvas = 1024;
    private const int SmartDeltaBlockSize = 8;
    private const int DeltaSpanMergeGapPixels = 6;

    private static readonly int[] SmartDeltaLevels =
    [
        0,
        2,
        3,
    ];

    private readonly record struct PlannedFrame(
        int SourceIndex,
        int DelayMs);

    private sealed record Candidate(
        byte[]? Bytes,
        long EncodedBytes,
        int StorageWidth,
        int StorageHeight,
        int FrameCount,
        int Fps,
        int DurationMs,
        PixelProPackedColorMode ColorMode,
        bool ExceededLimit);

    public static PixelProPackedAnimationResult EncodeBest(
        string path,
        ScreensaverScaleMode scaleMode,
        int maxDurationSeconds)
    {
        maxDurationSeconds =
            Math.Clamp(
                maxDurationSeconds,
                5,
                30);

        using Drawing.Image image =
            Drawing.Image.FromFile(path);

        if (image.Width < 1 ||
            image.Height < 1 ||
            image.Width > MaxCanvas ||
            image.Height > MaxCanvas)
        {
            throw new NotSupportedException(
                "PIXEL PRO GIF canvas must be between 1×1 and 1024×1024.");
        }

        var dimension =
            new FrameDimension(
                image.FrameDimensionsList[0]);

        int sourceFrameCount =
            Math.Max(
                1,
                image.GetFrameCount(dimension));

        int[] sourceDelays =
            ReadGifFrameDelaysMs(
                image,
                sourceFrameCount);

        int maxDurationMs =
            maxDurationSeconds *
            1000;

        PixelProPackedColorMode[] colorPriority =
        [
            PixelProPackedColorMode.Rgb888,
            PixelProPackedColorMode.Rgb565,
        ];

        // Quality priority is strict and lexicographic:
        // 1) color depth, 2) FPS, 3) storage resolution.
        // This means all RGB888 candidates are considered before RGB565.
        // Within a color mode, a higher FPS is preferred even if that means
        // using 360×240 instead of 480×320 at that FPS.
        foreach (PixelProPackedColorMode colorMode in colorPriority)
        {
            foreach ((int width, int height, int fps) in BuildQualityLadder())
            {
                foreach (int smartDeltaLevel in SmartDeltaLevels)
                {
                    Candidate candidate =
                        EncodeCandidate(
                            image,
                            dimension,
                            sourceDelays,
                            sourceFrameCount,
                            width,
                            height,
                            fps,
                            maxDurationMs,
                            colorMode,
                            scaleMode,
                            smartDeltaLevel,
                            HardTargetBytes);

                    if (!candidate.ExceededLimit &&
                        candidate.Bytes is not null &&
                        candidate.Bytes.Length <=
                            HardTargetBytes)
                    {
                        return ToResult(
                            candidate,
                            scaleMode,
                            new FileInfo(path).Length,
                            false);
                    }
                }
            }
        }

        throw new InvalidOperationException(
            "PIXEL PRO could not keep this GIF within 2 MiB while preserving at least RGB565, 15 FPS, and 360×240. Shorten or simplify the GIF.");
    }

    private static PixelProPackedAnimationResult ToResult(
        Candidate candidate,
        ScreensaverScaleMode scaleMode,
        long sourceBytes,
        bool emergency) =>
        new(
            candidate.Bytes ??
                throw new InvalidOperationException(
                    "Packed animation payload is missing."),
            candidate.StorageWidth,
            candidate.StorageHeight,
            candidate.FrameCount,
            candidate.Fps,
            candidate.DurationMs,
            candidate.ColorMode,
            scaleMode,
            sourceBytes,
            emergency);

    private static IReadOnlyList<(int Width, int Height, int Fps)>
        BuildQualityLadder()
    {
        var result =
            new List<(int, int, int)>();

        void Add(
            int width,
            int height,
            int fps)
        {
            var item =
                (width, height, fps);

            if (!result.Contains(item))
                result.Add(item);
        }

        // FPS outranks resolution: for each FPS level, try native 480×320
        // first and then 360×240. Never go below 360×240.
        foreach (int fps in new[] { 60, 50, 40, 30, 25, 20, 15 })
        {
            Add(480, 320, fps);
            Add(360, 240, fps);
        }

        return result;
    }

    private static Candidate EncodeCandidate(
        Drawing.Image image,
        FrameDimension dimension,
        IReadOnlyList<int> sourceDelays,
        int sourceFrameCount,
        int storageWidth,
        int storageHeight,
        int fps,
        int maxDurationMs,
        PixelProPackedColorMode colorMode,
        ScreensaverScaleMode scaleMode,
        int smartDeltaLevel,
        int abortAfterBytes)
    {
        List<PlannedFrame> plan =
            BuildFramePlan(
                sourceDelays,
                sourceFrameCount,
                fps,
                maxDurationMs);

        int durationMs =
            plan.Sum(
                frame =>
                    frame.DelayMs);

        // EncodeBest only emits RGB888/RGB565 now. Palette decoding helpers
        // remain in this file for backward compatibility with older PXQ data.
        Drawing.Color[] palette =
            Array.Empty<Drawing.Color>();

        byte[]? lookup =
            null;

        using var output =
            new MemoryStream(
                Math.Min(
                    abortAfterBytes,
                    1024 * 1024));

        WriteAscii(
            output,
            "PXQ1");

        output.WriteByte(
            (byte)colorMode);

        // bit0=delta frames, bit1=RLE
        output.WriteByte(0x03);

        WriteU16(
            output,
            storageWidth);

        WriteU16(
            output,
            storageHeight);

        WriteU16(
            output,
            DisplayWidth);

        WriteU16(
            output,
            DisplayHeight);

        WriteU16(
            output,
            plan.Count);

        WriteU16(
            output,
            fps);

        WriteU32(
            output,
            durationMs);

        WriteU16(
            output,
            palette.Length);

        WriteU16(
            output,
            0);

        foreach (Drawing.Color color in palette)
        {
            output.WriteByte(color.R);
            output.WriteByte(color.G);
            output.WriteByte(color.B);
        }

        int pixelCount =
            storageWidth *
            storageHeight;

        uint[] previous =
            Enumerable
                .Repeat(
                    uint.MaxValue,
                    pixelCount)
                .ToArray();

        var current =
            new uint[pixelCount];

        using var frameData =
            new MemoryStream();

        foreach (PlannedFrame planned in plan)
        {
            using Drawing.Bitmap logical =
                RenderLogicalFrame(
                    image,
                    dimension,
                    planned.SourceIndex,
                    scaleMode);

            using Drawing.Bitmap stored =
                ResizeBitmap(
                    logical,
                    storageWidth,
                    storageHeight);

            FillCodes(
                stored,
                colorMode,
                lookup,
                current);

            ApplySmartDeltaFilter(
                current,
                previous,
                storageWidth,
                storageHeight,
                colorMode,
                palette,
                smartDeltaLevel);

            frameData.SetLength(0);
            frameData.Position = 0;

            int spanCount =
                EncodeDeltaSpans(
                    frameData,
                    current,
                    previous,
                    storageWidth,
                    storageHeight,
                    colorMode);

            WriteU16(
                output,
                Math.Clamp(
                    planned.DelayMs,
                    1,
                    ushort.MaxValue));

            WriteU16(
                output,
                spanCount);

            frameData.Position = 0;
            frameData.CopyTo(output);

            (previous, current) =
                (current, previous);

            if (output.Length >
                abortAfterBytes)
            {
                return new Candidate(
                    null,
                    output.Length,
                    storageWidth,
                    storageHeight,
                    plan.Count,
                    fps,
                    durationMs,
                    colorMode,
                    true);
            }
        }

        return new Candidate(
            output.ToArray(),
            output.Length,
            storageWidth,
            storageHeight,
            plan.Count,
            fps,
            durationMs,
            colorMode,
            false);
    }

    private static void ApplySmartDeltaFilter(
        uint[] current,
        IReadOnlyList<uint> previous,
        int width,
        int height,
        PixelProPackedColorMode mode,
        IReadOnlyList<Drawing.Color> palette,
        int level)
    {
        if (level <= 0)
            return;

        int channelThreshold =
            level switch
            {
                1 => 6,
                2 => 10,
                _ => 16,
            };

        int distanceThreshold =
            channelThreshold *
            channelThreshold *
            3;

        for (int i = 0;
             i < current.Length;
             i++)
        {
            uint oldCode =
                previous[i];

            if (oldCode == uint.MaxValue ||
                current[i] == oldCode)
            {
                continue;
            }

            if (CodeColorDistanceSquared(
                    current[i],
                    oldCode,
                    mode,
                    palette) <=
                distanceThreshold)
            {
                // Keep the already-displayed pixel. This removes tiny palette
                // flicker/gradient noise without creating a new wire-format.
                current[i] =
                    oldCode;
            }
        }

        int sparseLimit =
            level switch
            {
                1 => 1,
                2 => 3,
                _ => 6,
            };

        // Remove isolated changes inside 8×8 blocks. Real moving edges survive
        // because they affect more pixels, while dithering/noise often does not.
        for (int blockY = 0;
             blockY < height;
             blockY += SmartDeltaBlockSize)
        {
            int blockHeight =
                Math.Min(
                    SmartDeltaBlockSize,
                    height - blockY);

            for (int blockX = 0;
                 blockX < width;
                 blockX += SmartDeltaBlockSize)
            {
                int blockWidth =
                    Math.Min(
                        SmartDeltaBlockSize,
                        width - blockX);

                int changed =
                    0;

                for (int y = 0;
                     y < blockHeight;
                     y++)
                {
                    int row =
                        (blockY + y) *
                        width +
                        blockX;

                    for (int x = 0;
                         x < blockWidth;
                         x++)
                    {
                        int index =
                            row +
                            x;

                        if (previous[index] != uint.MaxValue &&
                            current[index] != previous[index])
                        {
                            changed++;
                        }
                    }
                }

                if (changed == 0 ||
                    changed > sparseLimit)
                {
                    continue;
                }

                for (int y = 0;
                     y < blockHeight;
                     y++)
                {
                    int row =
                        (blockY + y) *
                        width +
                        blockX;

                    for (int x = 0;
                         x < blockWidth;
                         x++)
                    {
                        int index =
                            row +
                            x;

                        if (previous[index] != uint.MaxValue &&
                            current[index] != previous[index])
                        {
                            current[index] =
                                previous[index];
                        }
                    }
                }
            }
        }
    }

    private static int CodeColorDistanceSquared(
        uint a,
        uint b,
        PixelProPackedColorMode mode,
        IReadOnlyList<Drawing.Color> palette)
    {
        (int ar, int ag, int ab) =
            DecodeCodeColor(
                a,
                mode,
                palette);

        (int br, int bg, int bb) =
            DecodeCodeColor(
                b,
                mode,
                palette);

        int dr =
            ar -
            br;

        int dg =
            ag -
            bg;

        int db =
            ab -
            bb;

        return
            dr * dr +
            dg * dg +
            db * db;
    }

    private static (int R, int G, int B) DecodeCodeColor(
        uint value,
        PixelProPackedColorMode mode,
        IReadOnlyList<Drawing.Color> palette)
    {
        if (mode ==
            PixelProPackedColorMode.Rgb888)
        {
            return
            (
                (int)((value >> 16) & 0xFF),
                (int)((value >> 8) & 0xFF),
                (int)(value & 0xFF)
            );
        }

        if (mode ==
            PixelProPackedColorMode.Rgb565)
        {
            int r5 =
                (int)((value >> 11) & 0x1F);

            int g6 =
                (int)((value >> 5) & 0x3F);

            int b5 =
                (int)(value & 0x1F);

            return
            (
                (r5 << 3) | (r5 >> 2),
                (g6 << 2) | (g6 >> 4),
                (b5 << 3) | (b5 >> 2)
            );
        }

        if (palette.Count == 0)
            return (0, 0, 0);

        int index =
            Math.Clamp(
                (int)value,
                0,
                palette.Count - 1);

        Drawing.Color color =
            palette[index];

        return
        (
            color.R,
            color.G,
            color.B
        );
    }

    private static int EncodeDeltaSpans(
        Stream output,
        IReadOnlyList<uint> current,
        IReadOnlyList<uint> previous,
        int width,
        int height,
        PixelProPackedColorMode mode)
    {
        int spans = 0;

        // Split sparse rows into several spans instead of serializing the
        // entire area between the first and last changed pixel. Nearby changes
        // are still merged so busy rows do not explode into tiny headers.
        for (int y = 0;
             y < height;
             y++)
        {
            int row =
                y *
                width;

            int x =
                0;

            while (x < width)
            {
                while (x < width &&
                       current[row + x] ==
                           previous[row + x])
                {
                    x++;
                }

                if (x >= width)
                    break;

                int start =
                    x;

                int lastChanged =
                    x;

                int scan =
                    x +
                    1;

                int gap =
                    0;

                while (scan < width)
                {
                    if (current[row + scan] !=
                        previous[row + scan])
                    {
                        lastChanged =
                            scan;

                        gap =
                            0;
                    }
                    else
                    {
                        gap++;

                        if (gap >
                            DeltaSpanMergeGapPixels)
                        {
                            break;
                        }
                    }

                    scan++;
                }

                int count =
                    lastChanged -
                    start +
                    1;

                WriteU16(
                    output,
                    y);

                WriteU16(
                    output,
                    start);

                WriteU16(
                    output,
                    count);

                EncodeRunRle(
                    output,
                    current,
                    row + start,
                    count,
                    mode);

                spans++;

                x =
                    lastChanged +
                    1;
            }
        }

        return spans;
    }

    private static void EncodeRunRle(
        Stream output,
        IReadOnlyList<uint> values,
        int offset,
        int count,
        PixelProPackedColorMode mode)
    {
        int index = 0;

        while (index < count)
        {
            int repeat =
                1;

            while (index + repeat < count &&
                   repeat < 128 &&
                   values[offset + index + repeat] ==
                   values[offset + index])
            {
                repeat++;
            }

            if (repeat >= 3)
            {
                output.WriteByte(
                    (byte)(
                        0x80 |
                        (repeat - 1)));

                WriteSingleCode(
                    output,
                    values[offset + index],
                    mode);

                index +=
                    repeat;

                continue;
            }

            int literalStart =
                index;

            int literalCount =
                0;

            while (index < count &&
                   literalCount < 128)
            {
                repeat = 1;

                while (index + repeat < count &&
                       repeat < 128 &&
                       values[offset + index + repeat] ==
                       values[offset + index])
                {
                    repeat++;
                }

                if (repeat >= 3 &&
                    literalCount > 0)
                {
                    break;
                }

                index++;
                literalCount++;

                if (repeat >= 3)
                    break;
            }

            output.WriteByte(
                (byte)(
                    literalCount - 1));

            WriteLiteralCodes(
                output,
                values,
                offset +
                    literalStart,
                literalCount,
                mode);
        }
    }

    private static void WriteSingleCode(
        Stream output,
        uint value,
        PixelProPackedColorMode mode)
    {
        switch (mode)
        {
            case PixelProPackedColorMode.Rgb888:
                output.WriteByte(
                    (byte)(
                        (value >> 16) &
                        0xFF));

                output.WriteByte(
                    (byte)(
                        (value >> 8) &
                        0xFF));

                output.WriteByte(
                    (byte)(
                        value &
                        0xFF));
                break;

            case PixelProPackedColorMode.Rgb565:
                WriteU16(
                    output,
                    (int)value);
                break;

            default:
                output.WriteByte(
                    (byte)value);
                break;
        }
    }

    private static void WriteLiteralCodes(
        Stream output,
        IReadOnlyList<uint> values,
        int offset,
        int count,
        PixelProPackedColorMode mode)
    {
        if (mode ==
            PixelProPackedColorMode.Rgb888)
        {
            for (int i = 0; i < count; i++)
                WriteSingleCode(
                    output,
                    values[offset + i],
                    mode);

            return;
        }

        if (mode ==
            PixelProPackedColorMode.Rgb565)
        {
            for (int i = 0; i < count; i++)
                WriteU16(
                    output,
                    (int)values[offset + i]);

            return;
        }

        int bits =
            PaletteBits(
                mode);

        if (bits == 8)
        {
            for (int i = 0; i < count; i++)
                output.WriteByte(
                    (byte)values[offset + i]);

            return;
        }

        int bitBuffer = 0;
        int bitCount = 0;
        int mask =
            (1 << bits) -
            1;

        for (int i = 0; i < count; i++)
        {
            bitBuffer |=
                ((int)values[offset + i] &
                 mask) <<
                bitCount;

            bitCount +=
                bits;

            while (bitCount >= 8)
            {
                output.WriteByte(
                    (byte)(
                        bitBuffer &
                        0xFF));

                bitBuffer >>=
                    8;

                bitCount -=
                    8;
            }
        }

        if (bitCount > 0)
        {
            output.WriteByte(
                (byte)(
                    bitBuffer &
                    0xFF));
        }
    }

    private static void FillCodes(
        Drawing.Bitmap bitmap,
        PixelProPackedColorMode mode,
        byte[]? paletteLookup,
        uint[] result)
    {
        int width =
            bitmap.Width;

        int height =
            bitmap.Height;

        if (result.Length <
            width * height)
        {
            throw new ArgumentException(
                "PIXEL code buffer is too small.",
                nameof(result));
        }

        Drawing.Rectangle rect =
            new(
                0,
                0,
                width,
                height);

        BitmapData data =
            bitmap.LockBits(
                rect,
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

        try
        {
            int stride =
                data.Stride;

            int rowBytes =
                Math.Abs(
                    stride);

            var row =
                new byte[rowBytes];

            for (int y = 0;
                 y < height;
                 y++)
            {
                IntPtr rowPtr =
                    IntPtr.Add(
                        data.Scan0,
                        y *
                        stride);

                Marshal.Copy(
                    rowPtr,
                    row,
                    0,
                    rowBytes);

                int outOffset =
                    y *
                    width;

                for (int x = 0;
                     x < width;
                     x++)
                {
                    int p =
                        x *
                        3;

                    byte b =
                        row[p];

                    byte g =
                        row[p + 1];

                    byte r =
                        row[p + 2];

                    result[outOffset + x] =
                        mode switch
                        {
                            PixelProPackedColorMode.Rgb888 =>
                                (uint)(
                                    (r << 16) |
                                    (g << 8) |
                                    b),

                            PixelProPackedColorMode.Rgb565 =>
                                (uint)(
                                    ((r & 0xF8) << 8) |
                                    ((g & 0xFC) << 3) |
                                    (b >> 3)),

                            _ =>
                                paletteLookup?[
                                    ((r >> 3) << 10) |
                                    ((g >> 3) << 5) |
                                    (b >> 3)] ??
                                0
                        };
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(
                data);
        }
    }

    private static Drawing.Color[] BuildAdaptivePalette(
        Drawing.Image image,
        FrameDimension dimension,
        IReadOnlyList<PlannedFrame> plan,
        int width,
        int height,
        ScreensaverScaleMode scaleMode,
        int colorCount)
    {
        var histogram =
            new int[
                32 *
                32 *
                32];

        int samples =
            Math.Min(
                20,
                plan.Count);

        for (int sample = 0;
             sample < samples;
             sample++)
        {
            int planIndex =
                samples == 1
                    ? 0
                    : (int)Math.Round(
                        sample *
                        (plan.Count - 1) /
                        (double)(samples - 1));

            using Drawing.Bitmap logical =
                RenderLogicalFrame(
                    image,
                    dimension,
                    plan[planIndex].SourceIndex,
                    scaleMode);

            using Drawing.Bitmap stored =
                ResizeBitmap(
                    logical,
                    width,
                    height);

            AddToHistogram(
                stored,
                histogram,
                samples);
        }

        int[] populated =
            Enumerable
                .Range(
                    0,
                    histogram.Length)
                .Where(
                    bin =>
                        histogram[bin] >
                        0)
                .OrderByDescending(
                    bin =>
                        histogram[bin])
                .ToArray();

        if (populated.Length == 0)
            return
            [
                Drawing.Color.Black
            ];

        if (colorCount >= 64)
        {
            return PadPalette(
                populated
                    .Take(
                        colorCount)
                    .Select(
                        BinToColor)
                    .ToArray(),
                colorCount);
        }

        int count =
            Math.Min(
                colorCount,
                populated.Length);

        var centers =
            populated
                .Take(
                    count)
                .Select(
                    bin =>
                    {
                        Drawing.Color c =
                            BinToColor(
                                bin);

                        return (
                            R: (double)c.R,
                            G: (double)c.G,
                            B: (double)c.B);
                    })
                .ToArray();

        for (int iteration = 0;
             iteration < 6;
             iteration++)
        {
            var sumR =
                new double[count];

            var sumG =
                new double[count];

            var sumB =
                new double[count];

            var weights =
                new long[count];

            foreach (int bin in populated)
            {
                Drawing.Color color =
                    BinToColor(
                        bin);

                int best =
                    0;

                double bestDistance =
                    double.MaxValue;

                for (int i = 0;
                     i < count;
                     i++)
                {
                    double dr =
                        color.R -
                        centers[i].R;

                    double dg =
                        color.G -
                        centers[i].G;

                    double db =
                        color.B -
                        centers[i].B;

                    double distance =
                        dr * dr +
                        dg * dg +
                        db * db;

                    if (distance <
                        bestDistance)
                    {
                        bestDistance =
                            distance;

                        best =
                            i;
                    }
                }

                int weight =
                    histogram[bin];

                sumR[best] +=
                    color.R *
                    weight;

                sumG[best] +=
                    color.G *
                    weight;

                sumB[best] +=
                    color.B *
                    weight;

                weights[best] +=
                    weight;
            }

            for (int i = 0;
                 i < count;
                 i++)
            {
                if (weights[i] <= 0)
                    continue;

                centers[i] =
                    (
                        sumR[i] /
                            weights[i],
                        sumG[i] /
                            weights[i],
                        sumB[i] /
                            weights[i]);
            }
        }

        return PadPalette(
            centers
                .Select(
                    center =>
                        Drawing.Color.FromArgb(
                            Math.Clamp(
                                (int)Math.Round(
                                    center.R),
                                0,
                                255),
                            Math.Clamp(
                                (int)Math.Round(
                                    center.G),
                                0,
                                255),
                            Math.Clamp(
                                (int)Math.Round(
                                    center.B),
                                0,
                                255)))
                .ToArray(),
            colorCount);
    }

    private static Drawing.Color[] PadPalette(
        IReadOnlyList<Drawing.Color> source,
        int requestedCount)
    {
        var result =
            new Drawing.Color[
                requestedCount];

        Drawing.Color fallback =
            source.Count > 0
                ? source[source.Count - 1]
                : Drawing.Color.Black;

        for (int i = 0;
             i < requestedCount;
             i++)
        {
            result[i] =
                i < source.Count
                    ? source[i]
                    : fallback;
        }

        return result;
    }

    private static void AddToHistogram(
        Drawing.Bitmap bitmap,
        int[] histogram,
        int sampleFrames)
    {
        int width =
            bitmap.Width;

        int height =
            bitmap.Height;

        int step =
            Math.Max(
                1,
                (int)Math.Sqrt(
                    width *
                    height *
                    Math.Max(1, sampleFrames) /
                    180000.0));

        Drawing.Rectangle rect =
            new(
                0,
                0,
                width,
                height);

        BitmapData data =
            bitmap.LockBits(
                rect,
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

        try
        {
            int stride =
                data.Stride;

            int rowBytes =
                Math.Abs(
                    stride);

            var row =
                new byte[rowBytes];

            for (int y = 0;
                 y < height;
                 y += step)
            {
                IntPtr rowPtr =
                    IntPtr.Add(
                        data.Scan0,
                        y *
                        stride);

                Marshal.Copy(
                    rowPtr,
                    row,
                    0,
                    rowBytes);

                for (int x = 0;
                     x < width;
                     x += step)
                {
                    int p =
                        x *
                        3;

                    byte b =
                        row[p];

                    byte g =
                        row[p + 1];

                    byte r =
                        row[p + 2];

                    int bin =
                        ((r >> 3) << 10) |
                        ((g >> 3) << 5) |
                        (b >> 3);

                    histogram[bin]++;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(
                data);
        }
    }

    private static byte[] BuildPaletteLookup(
        IReadOnlyList<Drawing.Color> palette)
    {
        var lookup =
            new byte[
                32 *
                32 *
                32];

        for (int bin = 0;
             bin < lookup.Length;
             bin++)
        {
            Drawing.Color color =
                BinToColor(
                    bin);

            int best =
                0;

            int bestDistance =
                int.MaxValue;

            for (int i = 0;
                 i < palette.Count;
                 i++)
            {
                int dr =
                    color.R -
                    palette[i].R;

                int dg =
                    color.G -
                    palette[i].G;

                int db =
                    color.B -
                    palette[i].B;

                int distance =
                    dr * dr +
                    dg * dg +
                    db * db;

                if (distance <
                    bestDistance)
                {
                    bestDistance =
                        distance;

                    best =
                        i;

                    if (distance == 0)
                        break;
                }
            }

            lookup[bin] =
                (byte)best;
        }

        return lookup;
    }

    private static Drawing.Color BinToColor(
        int bin)
    {
        int r =
            (((bin >> 10) &
              31) <<
             3) |
            4;

        int g =
            (((bin >> 5) &
              31) <<
             3) |
            4;

        int b =
            ((bin &
              31) <<
             3) |
            4;

        return Drawing.Color.FromArgb(
            Math.Min(
                255,
                r),
            Math.Min(
                255,
                g),
            Math.Min(
                255,
                b));
    }

    private static Drawing.Bitmap RenderLogicalFrame(
        Drawing.Image image,
        FrameDimension dimension,
        int sourceIndex,
        ScreensaverScaleMode scaleMode)
    {
        image.SelectActiveFrame(
            dimension,
            sourceIndex);

        using var source =
            new Drawing.Bitmap(
                image.Width,
                image.Height,
                PixelFormat.Format24bppRgb);

        using (Drawing.Graphics sourceGraphics =
               Drawing.Graphics.FromImage(
                   source))
        {
            sourceGraphics.Clear(
                Drawing.Color.Black);

            sourceGraphics.DrawImageUnscaled(
                image,
                0,
                0);
        }

        return RenderLogicalBitmap(
            source,
            scaleMode);
    }

    internal static Drawing.Bitmap RenderStaticLogical(
        Drawing.Bitmap source,
        ScreensaverScaleMode scaleMode) =>
        RenderLogicalBitmap(
            source,
            scaleMode);

    private static Drawing.Bitmap RenderLogicalBitmap(
        Drawing.Bitmap source,
        ScreensaverScaleMode scaleMode)
    {
        var logical =
            new Drawing.Bitmap(
                DisplayWidth,
                DisplayHeight,
                PixelFormat.Format24bppRgb);

        using Drawing.Graphics g =
            Drawing.Graphics.FromImage(
                logical);

        g.Clear(
            Drawing.Color.Black);

        g.InterpolationMode =
            Drawing2D.InterpolationMode.HighQualityBicubic;

        g.PixelOffsetMode =
            Drawing2D.PixelOffsetMode.HighQuality;

        g.CompositingQuality =
            Drawing2D.CompositingQuality.HighQuality;

        if (scaleMode ==
            ScreensaverScaleMode.Stretch)
        {
            g.DrawImage(
                source,
                0,
                0,
                DisplayWidth,
                DisplayHeight);

            return logical;
        }

        if (scaleMode ==
            ScreensaverScaleMode.Tile)
        {
            using var brush =
                new Drawing.TextureBrush(
                    source,
                    Drawing2D.WrapMode.Tile);

            g.FillRectangle(
                brush,
                0,
                0,
                DisplayWidth,
                DisplayHeight);

            return logical;
        }

        double fit =
            Math.Min(
                DisplayWidth /
                    (double)source.Width,
                DisplayHeight /
                    (double)source.Height);

        double fill =
            Math.Max(
                DisplayWidth /
                    (double)source.Width,
                DisplayHeight /
                    (double)source.Height);

        double scale =
            scaleMode switch
            {
                ScreensaverScaleMode.Fit =>
                    fit,

                ScreensaverScaleMode.Center =>
                    1.0,

                ScreensaverScaleMode.Span =>
                    fill,

                ScreensaverScaleMode.Fill =>
                    fill,

                _ =>
                    fill
            };

        int drawWidth =
            Math.Max(
                1,
                (int)Math.Round(
                    source.Width *
                    scale));

        int drawHeight =
            Math.Max(
                1,
                (int)Math.Round(
                    source.Height *
                    scale));

        int x =
            (DisplayWidth -
             drawWidth) /
            2;

        int y =
            (DisplayHeight -
             drawHeight) /
            2;

        g.DrawImage(
            source,
            x,
            y,
            drawWidth,
            drawHeight);

        return logical;
    }

    private static Drawing.Bitmap ResizeBitmap(
        Drawing.Bitmap source,
        int width,
        int height)
    {
        if (source.Width ==
                width &&
            source.Height ==
                height)
        {
            return new Drawing.Bitmap(
                source);
        }

        var output =
            new Drawing.Bitmap(
                width,
                height,
                PixelFormat.Format24bppRgb);

        using Drawing.Graphics g =
            Drawing.Graphics.FromImage(
                output);

        g.InterpolationMode =
            Drawing2D.InterpolationMode.HighQualityBicubic;

        g.PixelOffsetMode =
            Drawing2D.PixelOffsetMode.HighQuality;

        g.CompositingQuality =
            Drawing2D.CompositingQuality.HighQuality;

        g.DrawImage(
            source,
            0,
            0,
            width,
            height);

        return output;
    }

    private static List<PlannedFrame> BuildFramePlan(
        IReadOnlyList<int> sourceDelaysMs,
        int sourceFrameCount,
        int fps,
        int maxDurationMs)
    {
        int minFrameMs =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    1000.0 /
                    Math.Max(
                        1,
                        fps)));

        var result =
            new List<PlannedFrame>();

        int pendingDelay =
            0;

        int groupStart =
            0;

        int elapsed =
            0;

        for (int sourceIndex = 0;
             sourceIndex <
                 sourceFrameCount &&
             elapsed <
                 maxDurationMs;
             sourceIndex++)
        {
            if (pendingDelay == 0)
                groupStart =
                    sourceIndex;

            int sourceDelay =
                sourceIndex <
                    sourceDelaysMs.Count
                    ? sourceDelaysMs[sourceIndex]
                    : 100;

            sourceDelay =
                Math.Clamp(
                    sourceDelay,
                    10,
                    5000);

            int accepted =
                Math.Min(
                    sourceDelay,
                    maxDurationMs -
                    elapsed);

            pendingDelay +=
                accepted;

            elapsed +=
                accepted;

            bool flush =
                pendingDelay >=
                    minFrameMs ||
                sourceIndex ==
                    sourceFrameCount - 1 ||
                elapsed >=
                    maxDurationMs;

            if (!flush)
                continue;

            result.Add(
                new PlannedFrame(
                    groupStart,
                    pendingDelay));

            pendingDelay =
                0;
        }

        if (result.Count == 0)
        {
            result.Add(
                new PlannedFrame(
                    0,
                    Math.Min(
                        100,
                        maxDurationMs)));
        }

        return result;
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

    private static bool IsPaletteMode(
        PixelProPackedColorMode mode) =>
        mode >=
        PixelProPackedColorMode.Palette256;

    private static int PaletteCount(
        PixelProPackedColorMode mode) =>
        mode switch
        {
            PixelProPackedColorMode.Palette256 =>
                256,
            PixelProPackedColorMode.Palette16 =>
                16,
            PixelProPackedColorMode.Palette4 =>
                4,
            PixelProPackedColorMode.Palette2 =>
                2,
            _ =>
                0
        };

    private static int PaletteBits(
        PixelProPackedColorMode mode) =>
        mode switch
        {
            PixelProPackedColorMode.Palette256 =>
                8,
            PixelProPackedColorMode.Palette16 =>
                4,
            PixelProPackedColorMode.Palette4 =>
                2,
            PixelProPackedColorMode.Palette2 =>
                1,
            _ =>
                0
        };

    private static void WriteAscii(
        Stream output,
        string value)
    {
        foreach (char ch in value)
            output.WriteByte(
                (byte)ch);
    }

    private static void WriteU16(
        Stream output,
        int value)
    {
        output.WriteByte(
            (byte)(
                value &
                0xFF));

        output.WriteByte(
            (byte)(
                (value >>
                 8) &
                0xFF));
    }

    private static void WriteU32(
        Stream output,
        int value)
    {
        uint unsigned =
            unchecked(
                (uint)value);

        output.WriteByte(
            (byte)(
                unsigned &
                0xFF));

        output.WriteByte(
            (byte)(
                (unsigned >>
                 8) &
                0xFF));

        output.WriteByte(
            (byte)(
                (unsigned >>
                 16) &
                0xFF));

        output.WriteByte(
            (byte)(
                (unsigned >>
                 24) &
                0xFF));
    }
}
