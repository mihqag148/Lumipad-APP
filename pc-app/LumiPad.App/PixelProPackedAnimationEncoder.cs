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
/// native/palette color depths. New PIXEL PRO media keeps a quality floor of
/// 360×240 and Palette256 while using up to 1100 KiB when needed.
/// </summary>
internal static class PixelProPackedAnimationEncoder
{
    public const int DisplayWidth = 480;
    public const int DisplayHeight = 320;
    public const int PreferredMinBytes = 800 * 1024;
    public const int HardTargetBytes = 1100 * 1024;

    private const int MaxCanvas = 1024;

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

        PixelProPackedColorMode[] colorModes =
        [
            PixelProPackedColorMode.Rgb888,
            PixelProPackedColorMode.Rgb565,
            PixelProPackedColorMode.Palette256,
        ];

        foreach ((int width, int height, int fps) in BuildQualityLadder())
        {
            foreach (PixelProPackedColorMode mode in colorModes)
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
                        mode,
                        scaleMode,
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
                        fps < 15);
                }
            }
        }

        throw new InvalidOperationException(
            "PIXEL PRO could not keep this GIF within 1100 KiB while preserving at least 360×240 and Palette256. Shorten or simplify the GIF.");
    }

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

        // Preserve native panel resolution whenever practical, then trade
        // spatial detail for motion. Never go below 360×240.
        foreach (int fps in new[] { 60, 50, 40, 30 })
            Add(480, 320, fps);

        foreach (int fps in new[] { 60, 50, 40, 30 })
            Add(360, 240, fps);

        foreach (int fps in new[] { 25, 20 })
            Add(480, 320, fps);

        foreach (int fps in new[] { 25, 20 })
            Add(360, 240, fps);

        Add(480, 320, 15);
        Add(360, 240, 15);

        // Emergency temporal reduction keeps the requested resolution/color
        // floor instead of falling back to 240×160 or Palette16/4/2.
        foreach (int fps in new[] { 12, 10, 8, 6, 5 })
            Add(360, 240, fps);

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

        Drawing.Color[] palette =
            IsPaletteMode(colorMode)
                ? BuildAdaptivePalette(
                    image,
                    dimension,
                    plan,
                    storageWidth,
                    storageHeight,
                    scaleMode,
                    PaletteCount(colorMode))
                : Array.Empty<Drawing.Color>();

        byte[]? lookup =
            IsPaletteMode(colorMode)
                ? BuildPaletteLookup(
                    palette)
                : null;

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

        uint[] previous =
            Enumerable
                .Repeat(
                    uint.MaxValue,
                    storageWidth *
                    storageHeight)
                .ToArray();

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

            uint[] current =
                ToCodes(
                    stored,
                    colorMode,
                    lookup);

            using var frameData =
                new MemoryStream();

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

            previous =
                current;

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

    private static int EncodeDeltaSpans(
        Stream output,
        IReadOnlyList<uint> current,
        IReadOnlyList<uint> previous,
        int width,
        int height,
        PixelProPackedColorMode mode)
    {
        int spans = 0;

        // Keep at most one delta span per source row. A noisy frame can
        // otherwise create tens of thousands of tiny spans; merging from the
        // first changed pixel to the last changed pixel keeps the frame header
        // bounded while RLE still compresses repeated pixels inside the span.
        for (int y = 0;
             y < height;
             y++)
        {
            int row =
                y *
                width;

            int firstChanged =
                -1;

            int lastChanged =
                -1;

            for (int x = 0;
                 x < width;
                 x++)
            {
                if (current[row + x] ==
                    previous[row + x])
                {
                    continue;
                }

                if (firstChanged < 0)
                    firstChanged = x;

                lastChanged = x;
            }

            if (firstChanged < 0)
                continue;

            int count =
                lastChanged -
                firstChanged +
                1;

            WriteU16(
                output,
                y);

            WriteU16(
                output,
                firstChanged);

            WriteU16(
                output,
                count);

            EncodeRunRle(
                output,
                current,
                row + firstChanged,
                count,
                mode);

            spans++;
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

    private static uint[] ToCodes(
        Drawing.Bitmap bitmap,
        PixelProPackedColorMode mode,
        byte[]? paletteLookup)
    {
        int width =
            bitmap.Width;

        int height =
            bitmap.Height;

        var result =
            new uint[
                width *
                height];

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

        return result;
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

        for (int y = 0;
             y < height;
             y += step)
        {
            for (int x = 0;
                 x < width;
                 x += step)
            {
                Drawing.Color color =
                    bitmap.GetPixel(
                        x,
                        y);

                int bin =
                    ((color.R >> 3) << 10) |
                    ((color.G >> 3) << 5) |
                    (color.B >> 3);

                histogram[bin]++;
            }
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
