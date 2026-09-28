using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace LumiPad.App;

internal enum RynorPackedColorMode : byte
{
    Rgb565 = 0,
    Rgb332 = 1,
    Palette16 = 2,
}

internal sealed record RynorPackedAnimationResult(
    byte[] Bytes,
    int StorageWidth,
    int StorageHeight,
    int FrameCount,
    int Fps,
    int DurationMs,
    RynorPackedColorMode ColorMode,
    ScreensaverScaleMode ScaleMode,
    bool UsedFallbackQuality,
    bool PreservedSourceTiming);

/// <summary>
/// RYNOR ONE animation packer.
///
/// RYQ1 keeps the first frame complete, then stores only changed row spans.
/// Each span uses packet RLE.
///
/// RYNOR policy:
/// - Always keep the 320x172 display resolution. Never fall back to the old
///   160x86 -> 2x path.
/// - First try exact source timing at full resolution with RGB565, then RGB332.
/// - If the exact animation does not fit the 10 MiB GIF partition, reduce
///   temporal rate (30/25/20/15 FPS) and apply small smart-delta thresholds
///   while keeping 320x172.
/// - Frames are stored as changed row spans + packet RLE, so the keyboard can
///   update only the pixels that changed instead of decoding LZW and rewriting
///   the whole screen.
///
/// PIXEL PRO has a separate media pipeline and is untouched.
/// </summary>
internal static class RynorPackedAnimationEncoder
{
    public const int DisplayWidth = 320;
    public const int DisplayHeight = 172;

    // External W25Q128 layout:
    //   10 MiB GIF partition
    //   first 4 KiB reserved for firmware metadata
    // Keep an additional 12 KiB safety margin for future metadata growth.
    public const int GifPartitionBytes = 10 * 1024 * 1024;
    public const int SourceLosslessThresholdBytes = 10 * 1024 * 1024;
    // Large-source compression aims just below 10 MiB so it uses the new
    // storage budget without running into the metadata/safety margin.
    public const int CompressedTargetBytes =
        9 * 1024 * 1024 + 768 * 1024; // 9.75 MiB
    public const int HardTargetBytes =
        GifPartitionBytes - (16 * 1024);

    private const int MaxPackedFrames = ushort.MaxValue;
    private const int SAVER_HEADER_BYTES = 26;
    private const int DeltaSpanMergeGapPixels = 4;
    private const int P16DeltaMergeGapPixels = 6;
    private const int P16TemporalThreshold4 = 1;
    private const double P16FullFrameCutover = 0.72;

    private static readonly int[] SmartDeltaLevels =
    [
        0,
        2,
        4,
        6,
    ];

    private readonly record struct PlannedFrame(
        int SourceIndex,
        int DelayMs);

    private readonly record struct P16Rect(
        int X,
        int Y,
        int Width,
        int Height);

    private sealed record Candidate(
        byte[]? Bytes,
        long EncodedBytes,
        int StorageWidth,
        int StorageHeight,
        int FrameCount,
        int Fps,
        int DurationMs,
        RynorPackedColorMode ColorMode,
        bool ExceededLimit);

    public static RynorPackedAnimationResult? TryEncodePalette16DeltaBest(
        string path,
        ScreensaverScaleMode scaleMode)
    {
        using Drawing.Image image =
            Drawing.Image.FromFile(path);

        if (image.Width < 1 ||
            image.Height < 1 ||
            image.Width > 2048 ||
            image.Height > 2048)
        {
            return null;
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

        if (sourceFrameCount <= MaxPackedFrames &&
            sourceDelays.Length >= sourceFrameCount &&
            sourceDelays
                .Take(sourceFrameCount)
                .All(delay => delay >= 33))
        {
            List<PlannedFrame> exact =
                BuildExactFramePlan(
                    sourceDelays,
                    sourceFrameCount);

            var exactResult =
                EncodePalette16DeltaCandidate(
                    image,
                    dimension,
                    exact,
                    NominalSourceFps(sourceDelays),
                    scaleMode,
                    true);

            if (exactResult is not null)
                return exactResult;
        }

        foreach (int fps in new[] { 30, 25, 20, 15 })
        {
            List<PlannedFrame> plan =
                BuildFramePlan(
                    sourceDelays,
                    sourceFrameCount,
                    fps);

            if (plan.Count < 1 ||
                plan.Count > MaxPackedFrames)
            {
                continue;
            }

            var result =
                EncodePalette16DeltaCandidate(
                    image,
                    dimension,
                    plan,
                    fps,
                    scaleMode,
                    false);

            if (result is not null)
                return result;
        }

        return null;
    }

    public static RynorPackedAnimationResult? TryEncodePalette16Best(
        string path,
        ScreensaverScaleMode scaleMode)
    {
        using Drawing.Image image =
            Drawing.Image.FromFile(path);

        if (image.Width < 1 ||
            image.Height < 1 ||
            image.Width > 2048 ||
            image.Height > 2048)
        {
            return null;
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

        const int frameBytes =
            2 +
            (16 * 2) +
            (DisplayWidth * DisplayHeight / 2);

        bool exactTimingFits =
            sourceFrameCount <= MaxPackedFrames &&
            sourceDelays.Length >= sourceFrameCount &&
            sourceDelays
                .Take(sourceFrameCount)
                .All(delay => delay >= 33) &&
            SAVER_HEADER_BYTES +
                ((long)sourceFrameCount * frameBytes) <=
                    HardTargetBytes;

        if (exactTimingFits)
        {
            List<PlannedFrame> exact =
                BuildExactFramePlan(
                    sourceDelays,
                    sourceFrameCount);

            var result =
                EncodePalette16Candidate(
                    image,
                    dimension,
                    exact,
                    NominalSourceFps(sourceDelays),
                    scaleMode,
                    true);

            if (result is not null)
                return result;
        }

        foreach (int fps in new[] { 30, 25, 20, 15 })
        {
            List<PlannedFrame> plan =
                BuildFramePlan(
                    sourceDelays,
                    sourceFrameCount,
                    fps);

            if (plan.Count < 1 ||
                plan.Count > MaxPackedFrames ||
                SAVER_HEADER_BYTES +
                    ((long)plan.Count * frameBytes) >
                        HardTargetBytes)
            {
                continue;
            }

            var result =
                EncodePalette16Candidate(
                    image,
                    dimension,
                    plan,
                    fps,
                    scaleMode,
                    false);

            if (result is not null)
                return result;
        }

        return null;
    }

    public static RynorPackedAnimationResult? TryEncodeBest(
        string path,
        ScreensaverScaleMode scaleMode)
    {
        long sourceBytes =
            new FileInfo(path).Length;

        using Drawing.Image image =
            Drawing.Image.FromFile(path);

        if (image.Width < 1 ||
            image.Height < 1 ||
            image.Width > 2048 ||
            image.Height > 2048)
        {
            return null;
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

        if (sourceFrameCount <= MaxPackedFrames)
        {
            int sourceFps =
                NominalSourceFps(
                    sourceDelays);

            // Best quality first: exact source timing, 320x172 RGB565.
            foreach (RynorPackedColorMode exactColor in
                     new[]
                     {
                         RynorPackedColorMode.Rgb565,
                         RynorPackedColorMode.Rgb332,
                     })
            {
                Candidate exact =
                    EncodeCandidate(
                        image,
                        dimension,
                        sourceDelays,
                        sourceFrameCount,
                        DisplayWidth,
                        DisplayHeight,
                        sourceFps,
                        exactColor,
                        scaleMode,
                        0,
                        HardTargetBytes,
                        preserveSourceTiming: true);

                if (!exact.ExceededLimit &&
                    exact.Bytes is not null &&
                    exact.Bytes.Length <= HardTargetBytes)
                {
                    return new RynorPackedAnimationResult(
                        exact.Bytes,
                        exact.StorageWidth,
                        exact.StorageHeight,
                        exact.FrameCount,
                        exact.Fps,
                        exact.DurationMs,
                        exact.ColorMode,
                        scaleMode,
                        exactColor != RynorPackedColorMode.Rgb565,
                        true);
                }
            }
        }

        bool firstCandidate = true;

        // Keep spatial resolution native. Reduce temporal redundancy before
        // sacrificing color depth; do not silently return to 160x86.
        foreach ((int width, int height, int fps) in BuildQualityLadder())
        {
            foreach (RynorPackedColorMode colorMode in
                     new[]
                     {
                         RynorPackedColorMode.Rgb565,
                         RynorPackedColorMode.Rgb332,
                     })
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
                            colorMode,
                            scaleMode,
                            smartDeltaLevel,
                            CompressedTargetBytes,
                            preserveSourceTiming: false);

                    if (!candidate.ExceededLimit &&
                        candidate.Bytes is not null &&
                        candidate.Bytes.Length <= CompressedTargetBytes)
                    {
                        return new RynorPackedAnimationResult(
                            candidate.Bytes,
                            candidate.StorageWidth,
                            candidate.StorageHeight,
                            candidate.FrameCount,
                            candidate.Fps,
                            candidate.DurationMs,
                            candidate.ColorMode,
                            scaleMode,
                            !firstCandidate ||
                            colorMode == RynorPackedColorMode.Rgb332 ||
                            smartDeltaLevel > 0,
                            false);
                    }

                    firstCandidate = false;
                }
            }
        }

        // Returning null deliberately preserves the proven legacy path.
        return null;
    }

    private static int NominalSourceFps(
        IReadOnlyList<int> sourceDelays)
    {
        long durationMs =
            Math.Max(
                1L,
                sourceDelays
                    .Select(delay => Math.Max(1, delay))
                    .Sum(delay => (long)delay));

        double averageFps =
            sourceDelays.Count *
            1000.0 /
            durationMs;

        // GIF delay units are 10 ms, so the meaningful upper bound is 100 FPS.
        return Math.Clamp(
            (int)Math.Round(averageFps),
            1,
            100);
    }

    private static IReadOnlyList<(int Width, int Height, int Fps)>
        BuildQualityLadder()
    {
        var result =
            new List<(int Width, int Height, int Fps)>();

        // RYNOR ONE stays native 320x172. When storage is tight, reduce
        // temporal redundancy before ever changing spatial resolution.
        foreach (int fps in new[] { 30, 25, 20, 15 })
        {
            result.Add((320, 172, fps));
        }

        return result;
    }

    private static RynorPackedAnimationResult?
        EncodePalette16DeltaCandidate(
            Drawing.Image image,
            FrameDimension dimension,
            IReadOnlyList<PlannedFrame> plan,
            int fps,
            ScreensaverScaleMode scaleMode,
            bool preservedSourceTiming)
    {
        if (plan.Count < 1 ||
            plan.Count > MaxPackedFrames)
        {
            return null;
        }

        int durationMs =
            plan.Sum(frame => frame.DelayMs);

        using var output =
            new MemoryStream(
                Math.Min(
                    HardTargetBytes,
                    1024 * 1024));

        WriteAscii(output, "RYQ3");
        output.WriteByte(
            (byte)RynorPackedColorMode.Palette16);
        output.WriteByte(0x20);
        WriteU16(output, DisplayWidth);
        WriteU16(output, DisplayHeight);
        WriteU16(output, DisplayWidth);
        WriteU16(output, DisplayHeight);
        WriteU16(output, plan.Count);
        WriteU16(output, Math.Clamp(fps, 1, 100));
        WriteU32(output, durationMs);
        WriteU16(output, 16);
        WriteU16(output, 0);

        int pixelCount =
            DisplayWidth *
            DisplayHeight;

        var previous444 =
            Enumerable
                .Repeat(
                    ushort.MaxValue,
                    pixelCount)
                .ToArray();

        bool firstFrame = true;

        foreach (PlannedFrame planned in plan)
        {
            byte[] rgb =
                RenderRgb24(
                    image,
                    dimension,
                    planned.SourceIndex,
                    scaleMode);

            QuantizePalette16(
                rgb,
                out ushort[] palette,
                out byte[] packedIndices);

            ushort[] current444 =
                BuildP16Rgb444Frame(
                    palette,
                    packedIndices);

            List<P16Rect> rects =
                BuildP16DeltaRects(
                    current444,
                    previous444,
                    firstFrame);

            WriteU16(
                output,
                Math.Clamp(
                    planned.DelayMs,
                    1,
                    ushort.MaxValue));

            for (int i = 0; i < 16; i++)
                WriteU16(output, palette[i]);

            WriteU16(
                output,
                rects.Count);

            foreach (P16Rect rect in rects)
            {
                WriteU16(output, rect.Y);
                WriteU16(output, rect.X);
                WriteU16(output, rect.Width);
                WriteU16(output, rect.Height);

                WriteP16RectIndices(
                    output,
                    packedIndices,
                    rect);

                UpdateP16DisplayedState(
                    previous444,
                    current444,
                    rect);
            }

            firstFrame = false;

            if (output.Length >
                HardTargetBytes)
            {
                return null;
            }
        }

        return new RynorPackedAnimationResult(
            output.ToArray(),
            DisplayWidth,
            DisplayHeight,
            plan.Count,
            fps,
            durationMs,
            RynorPackedColorMode.Palette16,
            scaleMode,
            true,
            preservedSourceTiming);
    }

    private static ushort[] BuildP16Rgb444Frame(
        IReadOnlyList<ushort> palette,
        IReadOnlyList<byte> packedIndices)
    {
        int pixelCount =
            DisplayWidth *
            DisplayHeight;

        var palette444 =
            new ushort[16];

        for (int i = 0; i < 16; i++)
        {
            ushort value =
                palette[i];

            int r5 =
                (value >> 11) &
                0x1F;
            int g6 =
                (value >> 5) &
                0x3F;
            int b5 =
                value &
                0x1F;

            int r4 =
                (r5 * 15 + 15) /
                31;
            int g4 =
                (g6 * 15 + 31) /
                63;
            int b4 =
                (b5 * 15 + 15) /
                31;

            palette444[i] =
                (ushort)(
                    (r4 << 8) |
                    (g4 << 4) |
                    b4);
        }

        var result =
            new ushort[pixelCount];

        for (int pixel = 0;
             pixel < pixelCount;
             pixel += 2)
        {
            byte pair =
                packedIndices[
                    pixel / 2];

            result[pixel] =
                palette444[
                    pair >> 4];

            result[pixel + 1] =
                palette444[
                    pair & 0x0F];
        }

        return result;
    }

    private static int P16Distance4(
        ushort a,
        ushort b)
    {
        if (a == ushort.MaxValue ||
            b == ushort.MaxValue) {
            return int.MaxValue;
        }

        int ar =
            (a >> 8) &
            0x0F;
        int ag =
            (a >> 4) &
            0x0F;
        int ab =
            a &
            0x0F;

        int br =
            (b >> 8) &
            0x0F;
        int bg =
            (b >> 4) &
            0x0F;
        int bb =
            b &
            0x0F;

        return Math.Max(
            Math.Abs(ar - br),
            Math.Max(
                Math.Abs(ag - bg),
                Math.Abs(ab - bb)));
    }

    private static List<P16Rect> BuildP16DeltaRects(
        IReadOnlyList<ushort> current444,
        IReadOnlyList<ushort> previous444,
        bool firstFrame)
    {
        if (firstFrame)
            return BuildP16InterlacedFullFrameRects();

        var rects =
            new List<P16Rect>();

        long transmittedPixels = 0L;

        for (int y = 0;
             y < DisplayHeight;
             y++)
        {
            int row =
                y *
                DisplayWidth;

            int x = 0;

            while (x < DisplayWidth)
            {
                while (x < DisplayWidth &&
                       P16Distance4(
                           current444[row + x],
                           previous444[row + x]) <=
                           P16TemporalThreshold4)
                {
                    x++;
                }

                if (x >= DisplayWidth)
                    break;

                int start = x;
                int lastChanged = x;
                int gap = 0;
                int scan = x + 1;

                while (scan < DisplayWidth)
                {
                    bool changed =
                        P16Distance4(
                            current444[row + scan],
                            previous444[row + scan]) >
                        P16TemporalThreshold4;

                    if (changed)
                    {
                        lastChanged = scan;
                        gap = 0;
                    }
                    else
                    {
                        gap++;

                        if (gap >
                            P16DeltaMergeGapPixels)
                        {
                            break;
                        }
                    }

                    scan++;
                }

                int evenStart =
                    start &
                    ~1;

                int evenEnd =
                    Math.Min(
                        DisplayWidth,
                        (lastChanged + 2) &
                        ~1);

                int width =
                    evenEnd -
                    evenStart;

                if (width > 0)
                {
                    rects.Add(
                        new P16Rect(
                            evenStart,
                            y,
                            width,
                            1));

                    transmittedPixels +=
                        width;
                }

                x =
                    lastChanged +
                    1;
            }
        }

        double coverage =
            transmittedPixels /
            (double)(
                DisplayWidth *
                DisplayHeight);

        if (rects.Count > 768 ||
            coverage >=
                P16FullFrameCutover)
        {
            return BuildP16InterlacedFullFrameRects();
        }

        // Do not walk from the top row to the bottom row. Spread small updates
        // over four row phases so any remaining physical LCD update is spatially
        // interlaced instead of appearing as one visible downward wipe.
        return rects
            .OrderBy(rect =>
                rect.Y &
                0x03)
            .ThenBy(rect =>
                rect.Y)
            .ThenBy(rect =>
                rect.X)
            .ToList();
    }

    private static List<P16Rect>
        BuildP16InterlacedFullFrameRects()
    {
        const int BandRows = 4;
        const int PhaseStrideRows =
            BandRows * 4;

        var rects =
            new List<P16Rect>(
                (DisplayHeight +
                 BandRows -
                 1) /
                BandRows);

        for (int phase = 0;
             phase < 4;
             phase++)
        {
            for (int y =
                     phase *
                     BandRows;
                 y < DisplayHeight;
                 y += PhaseStrideRows)
            {
                int height =
                    Math.Min(
                        BandRows,
                        DisplayHeight - y);

                rects.Add(
                    new P16Rect(
                        0,
                        y,
                        DisplayWidth,
                        height));
            }
        }

        return rects;
    }

    private static void WriteP16RectIndices(
        Stream output,
        IReadOnlyList<byte> packedIndices,
        P16Rect rect)
    {
        int bytesPerFullRow =
            DisplayWidth /
            2;

        int bytesPerRectRow =
            rect.Width /
            2;

        int startByte =
            rect.X /
            2;

        for (int row = 0;
             row < rect.Height;
             row++)
        {
            int sourceOffset =
                (rect.Y + row) *
                    bytesPerFullRow +
                startByte;

            for (int i = 0;
                 i < bytesPerRectRow;
                 i++)
            {
                output.WriteByte(
                    packedIndices[
                        sourceOffset +
                        i]);
            }
        }
    }

    private static void UpdateP16DisplayedState(
        ushort[] previous444,
        IReadOnlyList<ushort> current444,
        P16Rect rect)
    {
        for (int row = 0;
             row < rect.Height;
             row++)
        {
            int offset =
                (rect.Y + row) *
                    DisplayWidth +
                rect.X;

            for (int x = 0;
                 x < rect.Width;
                 x++)
            {
                previous444[
                    offset + x] =
                    current444[
                        offset + x];
            }
        }
    }

    private static RynorPackedAnimationResult?
        EncodePalette16Candidate(
            Drawing.Image image,
            FrameDimension dimension,
            IReadOnlyList<PlannedFrame> plan,
            int fps,
            ScreensaverScaleMode scaleMode,
            bool preservedSourceTiming)
    {
        if (plan.Count < 1 ||
            plan.Count > MaxPackedFrames)
        {
            return null;
        }

        int durationMs =
            plan.Sum(frame => frame.DelayMs);

        using var output =
            new MemoryStream(
                Math.Min(
                    HardTargetBytes,
                    1024 * 1024));

        WriteAscii(output, "RYQ2");
        output.WriteByte(
            (byte)RynorPackedColorMode.Palette16);
        output.WriteByte(0x10);
        WriteU16(output, DisplayWidth);
        WriteU16(output, DisplayHeight);
        WriteU16(output, DisplayWidth);
        WriteU16(output, DisplayHeight);
        WriteU16(output, plan.Count);
        WriteU16(output, Math.Clamp(fps, 1, 100));
        WriteU32(output, durationMs);
        WriteU16(output, 16);
        WriteU16(output, 0);

        foreach (PlannedFrame planned in plan)
        {
            byte[] rgb =
                RenderRgb24(
                    image,
                    dimension,
                    planned.SourceIndex,
                    scaleMode);

            QuantizePalette16(
                rgb,
                out ushort[] palette,
                out byte[] packedIndices);

            WriteU16(
                output,
                Math.Clamp(
                    planned.DelayMs,
                    1,
                    ushort.MaxValue));

            for (int i = 0; i < 16; i++)
                WriteU16(output, palette[i]);

            output.Write(
                packedIndices,
                0,
                packedIndices.Length);

            if (output.Length >
                HardTargetBytes)
            {
                return null;
            }
        }

        return new RynorPackedAnimationResult(
            output.ToArray(),
            DisplayWidth,
            DisplayHeight,
            plan.Count,
            fps,
            durationMs,
            RynorPackedColorMode.Palette16,
            scaleMode,
            true,
            preservedSourceTiming);
    }

    private static byte[] RenderRgb24(
        Drawing.Image image,
        FrameDimension dimension,
        int sourceIndex,
        ScreensaverScaleMode scaleMode)
    {
        image.SelectActiveFrame(
            dimension,
            sourceIndex);

        using var canvas =
            new Drawing.Bitmap(
                DisplayWidth,
                DisplayHeight,
                PixelFormat.Format24bppRgb);

        using (Drawing.Graphics graphics =
               Drawing.Graphics.FromImage(
                   canvas))
        {
            graphics.Clear(
                Drawing.Color.Black);

            graphics.InterpolationMode =
                Drawing2D.InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode =
                Drawing2D.PixelOffsetMode.HighQuality;
            graphics.CompositingQuality =
                Drawing2D.CompositingQuality.HighQuality;

            DrawScaled(
                graphics,
                image,
                DisplayWidth,
                DisplayHeight,
                scaleMode);
        }

        var rgb =
            new byte[
                DisplayWidth *
                DisplayHeight *
                3];

        var rect =
            new Drawing.Rectangle(
                0,
                0,
                DisplayWidth,
                DisplayHeight);

        BitmapData data =
            canvas.LockBits(
                rect,
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

        try
        {
            int stride =
                Math.Abs(data.Stride);

            var raw =
                new byte[
                    stride *
                    DisplayHeight];

            Marshal.Copy(
                data.Scan0,
                raw,
                0,
                raw.Length);

            int destination = 0;

            for (int y = 0;
                 y < DisplayHeight;
                 y++)
            {
                int row =
                    data.Stride >= 0
                        ? y * stride
                        : (DisplayHeight - 1 - y) *
                          stride;

                for (int x = 0;
                     x < DisplayWidth;
                     x++)
                {
                    int source =
                        row +
                        x *
                        3;

                    rgb[destination++] =
                        raw[source + 2];
                    rgb[destination++] =
                        raw[source + 1];
                    rgb[destination++] =
                        raw[source];
                }
            }
        }
        finally
        {
            canvas.UnlockBits(data);
        }

        return rgb;
    }

    private static void QuantizePalette16(
        IReadOnlyList<byte> rgb,
        out ushort[] palette,
        out byte[] packedIndices)
    {
        var histogram =
            new int[4096];

        int pixelCount =
            rgb.Count /
            3;

        for (int pixel = 0;
             pixel < pixelCount;
             pixel++)
        {
            int o =
                pixel *
                3;

            int code =
                ((rgb[o] >> 4) << 8) |
                ((rgb[o + 1] >> 4) << 4) |
                (rgb[o + 2] >> 4);

            histogram[code]++;
        }

        int[] centers =
            Enumerable
                .Range(0, 4096)
                .Where(code => histogram[code] > 0)
                .OrderByDescending(code => histogram[code])
                .Take(16)
                .ToArray();

        if (centers.Length == 0)
            centers = [0];

        if (centers.Length < 16)
        {
            int original =
                centers.Length;

            Array.Resize(
                ref centers,
                16);

            for (int i = original;
                 i < 16;
                 i++)
            {
                centers[i] =
                    centers[i % original];
            }
        }

        // A few weighted k-means rounds over the 12-bit histogram give a much
        // better 16-color frame palette than a fixed RGBI palette while
        // keeping preprocessing cheap enough for the desktop app.
        for (int iteration = 0;
             iteration < 5;
             iteration++)
        {
            var sumR = new long[16];
            var sumG = new long[16];
            var sumB = new long[16];
            var weights = new long[16];

            for (int code = 0;
                 code < 4096;
                 code++)
            {
                int count =
                    histogram[code];

                if (count == 0)
                    continue;

                int r =
                    (code >> 8) &
                    0x0F;

                int g =
                    (code >> 4) &
                    0x0F;

                int b =
                    code &
                    0x0F;

                int nearest =
                    NearestCenter(
                        r,
                        g,
                        b,
                        centers);

                sumR[nearest] +=
                    (long)r *
                    count;
                sumG[nearest] +=
                    (long)g *
                    count;
                sumB[nearest] +=
                    (long)b *
                    count;
                weights[nearest] +=
                    count;
            }

            for (int i = 0;
                 i < 16;
                 i++)
            {
                if (weights[i] == 0)
                    continue;

                int r =
                    (int)Math.Clamp(
                        (sumR[i] +
                         weights[i] / 2) /
                        weights[i],
                        0,
                        15);

                int g =
                    (int)Math.Clamp(
                        (sumG[i] +
                         weights[i] / 2) /
                        weights[i],
                        0,
                        15);

                int b =
                    (int)Math.Clamp(
                        (sumB[i] +
                         weights[i] / 2) /
                        weights[i],
                        0,
                        15);

                centers[i] =
                    (r << 8) |
                    (g << 4) |
                    b;
            }
        }

        palette =
            new ushort[16];

        for (int i = 0;
             i < 16;
             i++)
        {
            int code =
                centers[i];

            byte r =
                (byte)(
                    ((code >> 8) &
                     0x0F) *
                    17);

            byte g =
                (byte)(
                    ((code >> 4) &
                     0x0F) *
                    17);

            byte b =
                (byte)(
                    (code &
                     0x0F) *
                    17);

            palette[i] =
                (ushort)ToRgb565(
                    r,
                    g,
                    b);
        }

        var lookup =
            new byte[4096];

        for (int code = 0;
             code < 4096;
             code++)
        {
            lookup[code] =
                (byte)NearestCenter(
                    (code >> 8) & 0x0F,
                    (code >> 4) & 0x0F,
                    code & 0x0F,
                    centers);
        }

        packedIndices =
            new byte[
                DisplayWidth *
                DisplayHeight /
                2];

        for (int pixel = 0;
             pixel < pixelCount;
             pixel += 2)
        {
            int o0 =
                pixel *
                3;

            int c0 =
                ((rgb[o0] >> 4) << 8) |
                ((rgb[o0 + 1] >> 4) << 4) |
                (rgb[o0 + 2] >> 4);

            int o1 =
                (pixel + 1) *
                3;

            int c1 =
                ((rgb[o1] >> 4) << 8) |
                ((rgb[o1 + 1] >> 4) << 4) |
                (rgb[o1 + 2] >> 4);

            packedIndices[
                pixel /
                2] =
                (byte)(
                    (lookup[c0] << 4) |
                    lookup[c1]);
        }
    }

    private static int NearestCenter(
        int r,
        int g,
        int b,
        IReadOnlyList<int> centers)
    {
        int best = 0;
        int bestDistance =
            int.MaxValue;

        for (int i = 0;
             i < centers.Count;
             i++)
        {
            int code =
                centers[i];

            int dr =
                r -
                ((code >> 8) &
                 0x0F);

            int dg =
                g -
                ((code >> 4) &
                 0x0F);

            int db =
                b -
                (code &
                 0x0F);

            int distance =
                dr * dr +
                dg * dg +
                db * db;

            if (distance <
                bestDistance)
            {
                bestDistance =
                    distance;
                best = i;
            }
        }

        return best;
    }

    private static Candidate EncodeCandidate(
        Drawing.Image image,
        FrameDimension dimension,
        IReadOnlyList<int> sourceDelays,
        int sourceFrameCount,
        int storageWidth,
        int storageHeight,
        int fps,
        RynorPackedColorMode colorMode,
        ScreensaverScaleMode scaleMode,
        int smartDeltaLevel,
        int abortAfterBytes,
        bool preserveSourceTiming)
    {
        List<PlannedFrame> plan =
            preserveSourceTiming
                ? BuildExactFramePlan(
                    sourceDelays,
                    sourceFrameCount)
                : BuildFramePlan(
                    sourceDelays,
                    sourceFrameCount,
                    fps);

        if (plan.Count < 1 ||
            plan.Count > MaxPackedFrames)
        {
            return new Candidate(
                null,
                0,
                storageWidth,
                storageHeight,
                plan.Count,
                fps,
                0,
                colorMode,
                true);
        }

        int durationMs =
            plan.Sum(frame => frame.DelayMs);

        using var output =
            new MemoryStream(
                Math.Min(
                    abortAfterBytes,
                    1024 * 1024));

        WriteAscii(
            output,
            "RYQ1");

        output.WriteByte(
            (byte)colorMode);

        // bit0 = delta spans, bit1 = packet RLE.
        output.WriteByte(0x03);

        WriteU16(output, storageWidth);
        WriteU16(output, storageHeight);
        WriteU16(output, DisplayWidth);
        WriteU16(output, DisplayHeight);
        WriteU16(output, plan.Count);
        WriteU16(output, fps);
        WriteU32(output, durationMs);
        WriteU16(output, 0); // palette count (reserved for future use)
        WriteU16(output, 0);

        int pixelCount =
            storageWidth *
            storageHeight;

        uint[] previous =
            Enumerable
                .Repeat(
                    uint.MaxValue,
                    pixelCount)
                .ToArray();

        using var frameData =
            new MemoryStream();

        foreach (PlannedFrame planned in plan)
        {
            uint[] current =
                RenderCodes(
                    image,
                    dimension,
                    planned.SourceIndex,
                    storageWidth,
                    storageHeight,
                    colorMode,
                    scaleMode);

            ApplySmartDelta(
                current,
                previous,
                colorMode,
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

            previous = current;

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

    private static List<PlannedFrame> BuildExactFramePlan(
        IReadOnlyList<int> sourceDelays,
        int sourceFrameCount)
    {
        if (sourceFrameCount < 1 ||
            sourceFrameCount > MaxPackedFrames ||
            sourceDelays.Count < sourceFrameCount)
        {
            return new List<PlannedFrame>();
        }

        var result =
            new List<PlannedFrame>(
                sourceFrameCount);

        for (int index = 0;
             index < sourceFrameCount;
             ++index)
        {
            result.Add(
                new PlannedFrame(
                    index,
                    Math.Clamp(
                        sourceDelays[index],
                        1,
                        ushort.MaxValue)));
        }

        return result;
    }

    private static List<PlannedFrame> BuildFramePlan(
        IReadOnlyList<int> sourceDelays,
        int sourceFrameCount,
        int fps)
    {
        int loopMs =
            Math.Max(
                1,
                sourceDelays.Sum());

        int minIntervalMs =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    1000.0 /
                    Math.Max(1, fps)));

        bool exactTimingFits =
            sourceFrameCount <= MaxPackedFrames &&
            sourceDelays.All(
                delay =>
                    delay >= minIntervalMs);

        if (exactTimingFits)
        {
            var exact =
                new List<PlannedFrame>(
                    sourceFrameCount);

            for (int index = 0;
                 index < sourceFrameCount;
                 ++index)
            {
                exact.Add(
                    new PlannedFrame(
                        index,
                        Math.Clamp(
                            sourceDelays[index],
                            1,
                            ushort.MaxValue)));
            }

            return exact;
        }

        int maxFramesByRate =
            Math.Max(
                1,
                loopMs /
                minIntervalMs);

        int count =
            Math.Clamp(
                Math.Min(
                    sourceFrameCount,
                    maxFramesByRate),
                1,
                MaxPackedFrames);

        int outputLoopMs =
            Math.Max(
                loopMs,
                count *
                minIntervalMs);

        int baseDelay =
            outputLoopMs /
            count;

        int remainder =
            outputLoopMs %
            count;

        var cumulative =
            new int[sourceFrameCount];

        int running = 0;
        for (int index = 0;
             index < sourceFrameCount;
             ++index)
        {
            running +=
                sourceDelays[index];

            cumulative[index] =
                running;
        }

        var result =
            new List<PlannedFrame>(
                count);

        for (int index = 0;
             index < count;
             ++index)
        {
            double sourceTime =
                index *
                (loopMs /
                 (double)count);

            int sourceIndex = 0;
            while (sourceIndex <
                       sourceFrameCount -
                           1 &&
                   sourceTime >=
                       cumulative[sourceIndex])
            {
                sourceIndex++;
            }

            int delay =
                baseDelay +
                (index < remainder
                    ? 1
                    : 0);

            result.Add(
                new PlannedFrame(
                    sourceIndex,
                    Math.Clamp(
                        delay,
                        1,
                        ushort.MaxValue)));
        }

        return result;
    }

    private static uint[] RenderCodes(
        Drawing.Image image,
        FrameDimension dimension,
        int sourceIndex,
        int width,
        int height,
        RynorPackedColorMode colorMode,
        ScreensaverScaleMode scaleMode)
    {
        image.SelectActiveFrame(
            dimension,
            sourceIndex);

        using var canvas =
            new Drawing.Bitmap(
                width,
                height,
                PixelFormat.Format24bppRgb);

        using (Drawing.Graphics graphics =
               Drawing.Graphics.FromImage(
                   canvas))
        {
            graphics.Clear(
                Drawing.Color.Black);

            graphics.InterpolationMode =
                Drawing2D.InterpolationMode.HighQualityBicubic;

            graphics.PixelOffsetMode =
                Drawing2D.PixelOffsetMode.HighQuality;

            graphics.CompositingQuality =
                Drawing2D.CompositingQuality.HighQuality;

            graphics.SmoothingMode =
                Drawing2D.SmoothingMode.HighQuality;

            DrawScaled(
                graphics,
                image,
                width,
                height,
                scaleMode);
        }

        var codes =
            new uint[
                width *
                height];

        var rect =
            new Drawing.Rectangle(
                0,
                0,
                width,
                height);

        BitmapData data =
            canvas.LockBits(
                rect,
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

        try
        {
            int stride =
                Math.Abs(
                    data.Stride);

            var bytes =
                new byte[
                    stride *
                    height];

            Marshal.Copy(
                data.Scan0,
                bytes,
                0,
                bytes.Length);

            for (int y = 0;
                 y < height;
                 ++y)
            {
                int row =
                    data.Stride >= 0
                        ? y * stride
                        : (height - 1 - y) *
                          stride;

                for (int x = 0;
                     x < width;
                     ++x)
                {
                    int offset =
                        row +
                        x *
                        3;

                    byte b =
                        bytes[offset];

                    byte g =
                        bytes[offset + 1];

                    byte r =
                        bytes[offset + 2];

                    codes[
                        y *
                            width +
                        x] =
                        colorMode ==
                        RynorPackedColorMode.Rgb565
                            ? ToRgb565(
                                r,
                                g,
                                b)
                            : ToRgb332(
                                r,
                                g,
                                b);
                }
            }
        }
        finally
        {
            canvas.UnlockBits(
                data);
        }

        return codes;
    }

    private static void DrawScaled(
        Drawing.Graphics graphics,
        Drawing.Image source,
        int width,
        int height,
        ScreensaverScaleMode scaleMode)
    {
        switch (scaleMode)
        {
            case ScreensaverScaleMode.Stretch:
                graphics.DrawImage(
                    source,
                    0,
                    0,
                    width,
                    height);
                break;

            case ScreensaverScaleMode.Fit:
            {
                double scale =
                    Math.Min(
                        width /
                        (double)source.Width,
                        height /
                        (double)source.Height);

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

                graphics.DrawImage(
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
                    Math.Min(
                        source.Width,
                        width);

                int drawH =
                    Math.Min(
                        source.Height,
                        height);

                int sx =
                    Math.Max(
                        0,
                        (source.Width -
                         drawW) /
                        2);

                int sy =
                    Math.Max(
                        0,
                        (source.Height -
                         drawH) /
                        2);

                graphics.DrawImage(
                    source,
                    new Drawing.Rectangle(
                        (width - drawW) / 2,
                        (height - drawH) / 2,
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
                            source.Width *
                            scale));

                int tileH =
                    Math.Max(
                        8,
                        (int)Math.Round(
                            source.Height *
                            scale));

                using var tile =
                    new Drawing.Bitmap(
                        tileW,
                        tileH);

                using (Drawing.Graphics tileGraphics =
                       Drawing.Graphics.FromImage(
                           tile))
                {
                    tileGraphics.InterpolationMode =
                        Drawing2D.InterpolationMode.HighQualityBilinear;

                    tileGraphics.DrawImage(
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

                graphics.FillRectangle(
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
                        width /
                        (double)source.Width,
                        height /
                        (double)source.Height);

                if (scaleMode ==
                    ScreensaverScaleMode.Span)
                {
                    scale *=
                        1.08;
                }

                int drawW =
                    Math.Max(
                        1,
                        (int)Math.Ceiling(
                            source.Width *
                            scale));

                int drawH =
                    Math.Max(
                        1,
                        (int)Math.Ceiling(
                            source.Height *
                            scale));

                graphics.DrawImage(
                    source,
                    (width - drawW) / 2,
                    (height - drawH) / 2,
                    drawW,
                    drawH);
                break;
            }
        }
    }

    private static void ApplySmartDelta(
        uint[] current,
        IReadOnlyList<uint> previous,
        RynorPackedColorMode colorMode,
        int threshold)
    {
        if (threshold <= 0)
            return;

        for (int index = 0;
             index < current.Length;
             ++index)
        {
            uint oldValue =
                previous[index];

            if (oldValue ==
                uint.MaxValue)
            {
                continue;
            }

            DecodeRgb(
                current[index],
                colorMode,
                out int cr,
                out int cg,
                out int cb);

            DecodeRgb(
                oldValue,
                colorMode,
                out int pr,
                out int pg,
                out int pb);

            int distance =
                Math.Max(
                    Math.Abs(cr - pr),
                    Math.Max(
                        Math.Abs(cg - pg),
                        Math.Abs(cb - pb)));

            if (distance <= threshold)
            {
                current[index] =
                    oldValue;
            }
        }
    }

    private static int EncodeDeltaSpans(
        Stream output,
        IReadOnlyList<uint> current,
        IReadOnlyList<uint> previous,
        int width,
        int height,
        RynorPackedColorMode colorMode)
    {
        int spanCount = 0;

        for (int y = 0;
             y < height;
             ++y)
        {
            int rowOffset =
                y *
                width;

            int x = 0;

            while (x < width)
            {
                while (x < width &&
                       current[
                           rowOffset +
                           x] ==
                       previous[
                           rowOffset +
                           x])
                {
                    x++;
                }

                if (x >= width)
                    break;

                int start = x;
                int lastChanged = x;
                int gap = 0;
                int scan = x + 1;

                while (scan < width)
                {
                    bool changed =
                        current[
                            rowOffset +
                            scan] !=
                        previous[
                            rowOffset +
                            scan];

                    if (changed)
                    {
                        lastChanged =
                            scan;

                        gap = 0;
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

                EncodeRun(
                    output,
                    current,
                    rowOffset +
                        start,
                    count,
                    colorMode);

                spanCount++;
                x =
                    lastChanged +
                    1;
            }
        }

        return spanCount;
    }

    private static void EncodeRun(
        Stream output,
        IReadOnlyList<uint> values,
        int offset,
        int count,
        RynorPackedColorMode colorMode)
    {
        int index = 0;

        while (index < count)
        {
            int repeat =
                CountRepeat(
                    values,
                    offset +
                        index,
                    count -
                        index);

            if (repeat >= 3)
            {
                output.WriteByte(
                    (byte)(
                        0x80 |
                        (repeat -
                         1)));

                WriteCode(
                    output,
                    values[
                        offset +
                        index],
                    colorMode);

                index +=
                    repeat;

                continue;
            }

            int literalStart =
                index;

            int literalCount = 0;

            while (index < count &&
                   literalCount < 128)
            {
                repeat =
                    CountRepeat(
                        values,
                        offset +
                            index,
                        count -
                            index);

                if (repeat >= 3 &&
                    literalCount > 0)
                {
                    break;
                }

                if (repeat >= 3)
                {
                    break;
                }

                index++;
                literalCount++;
            }

            if (literalCount == 0)
            {
                // Defensive fallback; the repeated run will be handled on
                // the next loop iteration.
                continue;
            }

            output.WriteByte(
                (byte)(
                    literalCount -
                    1));

            for (int item = 0;
                 item < literalCount;
                 ++item)
            {
                WriteCode(
                    output,
                    values[
                        offset +
                        literalStart +
                        item],
                    colorMode);
            }
        }
    }

    private static int CountRepeat(
        IReadOnlyList<uint> values,
        int offset,
        int remaining)
    {
        int repeat = 1;
        int limit =
            Math.Min(
                128,
                remaining);

        while (repeat < limit &&
               values[
                   offset +
                   repeat] ==
               values[offset])
        {
            repeat++;
        }

        return repeat;
    }

    private static void WriteCode(
        Stream output,
        uint value,
        RynorPackedColorMode colorMode)
    {
        if (colorMode ==
            RynorPackedColorMode.Rgb565)
        {
            WriteU16(
                output,
                (int)value);
        }
        else
        {
            output.WriteByte(
                (byte)value);
        }
    }

    private static uint ToRgb565(
        byte r,
        byte g,
        byte b) =>
        (uint)(
            ((r & 0xF8) << 8) |
            ((g & 0xFC) << 3) |
            (b >> 3));

    private static uint ToRgb332(
        byte r,
        byte g,
        byte b) =>
        (uint)(
            ((r >> 5) << 5) |
            ((g >> 5) << 2) |
            (b >> 6));

    private static void DecodeRgb(
        uint value,
        RynorPackedColorMode colorMode,
        out int r,
        out int g,
        out int b)
    {
        if (colorMode ==
            RynorPackedColorMode.Rgb565)
        {
            r =
                (int)(
                    ((value >> 11) &
                     0x1F) *
                    255 /
                    31);

            g =
                (int)(
                    ((value >> 5) &
                     0x3F) *
                    255 /
                    63);

            b =
                (int)(
                    (value &
                     0x1F) *
                    255 /
                    31);

            return;
        }

        r =
            (int)(
                ((value >> 5) &
                 0x07) *
                255 /
                7);

        g =
            (int)(
                ((value >> 2) &
                 0x07) *
                255 /
                7);

        b =
            (int)(
                (value &
                 0x03) *
                255 /
                3);
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

                for (int index = 0;
                     index < entries;
                     ++index)
                {
                    int delayCs =
                        BitConverter.ToInt32(
                            item.Value,
                            index *
                            4);

                    int delayMs =
                        Math.Max(
                            1,
                            delayCs) *
                        10;

                    delays[index] =
                        Math.Clamp(
                            delayMs,
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

    private static void WriteAscii(
        Stream output,
        string value)
    {
        foreach (char ch in value)
        {
            output.WriteByte(
                (byte)ch);
        }
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
                (value >> 8) &
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
                (unsigned >> 8) &
                0xFF));

        output.WriteByte(
            (byte)(
                (unsigned >> 16) &
                0xFF));

        output.WriteByte(
            (byte)(
                (unsigned >> 24) &
                0xFF));
    }
}
