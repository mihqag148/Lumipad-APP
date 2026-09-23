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
    bool UsedFallbackQuality);

/// <summary>
/// RYNOR ONE animation packer.
///
/// RYQ1 keeps the first frame complete, then stores only changed row spans.
/// Each span uses packet RLE. The encoder progressively reduces temporal
/// detail/resolution only when the payload cannot fit the existing saver flash
/// partition. The hard target intentionally stays below 400 KB and below the
/// current RYNOR ONE saver partition payload capacity.
/// </summary>
internal static class RynorPackedAnimationEncoder
{
    public const int DisplayWidth = 320;
    public const int DisplayHeight = 172;

    // 0x56000 saver partition - 0x1000 metadata/data offset = 0x55000
    // available payload. Keep a few KiB of margin and stay < 400 KB.
    public const int HardTargetBytes = 336 * 1024;

    private const int MaxPackedFrames = 250;
    private const int DeltaSpanMergeGapPixels = 4;

    private static readonly int[] SmartDeltaLevels =
    [
        0,
        3,
        6,
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
        RynorPackedColorMode ColorMode,
        bool ExceededLimit);

    public static RynorPackedAnimationResult? TryEncodeBest(
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

        bool firstCandidate = true;

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
                            HardTargetBytes);

                    if (!candidate.ExceededLimit &&
                        candidate.Bytes is not null &&
                        candidate.Bytes.Length <= HardTargetBytes)
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
                            !firstCandidate);
                    }

                    firstCandidate = false;
                }
            }
        }

        // Returning null deliberately preserves the proven legacy path:
        // 25 x 160x86 RGB332 raw frames = 344000 bytes.
        return null;
    }

    private static IReadOnlyList<(int Width, int Height, int Fps)>
        BuildQualityLadder()
    {
        var result =
            new List<(int Width, int Height, int Fps)>();

        // Smoothness wins first, matching the PIXEL PRO policy. Within each
        // FPS tier, keep the highest spatial resolution that fits.
        foreach (int fps in new[] { 25, 20, 15 })
        {
            result.Add((320, 172, fps));
            result.Add((240, 129, fps));
            result.Add((160, 86, fps));
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
        RynorPackedColorMode colorMode,
        ScreensaverScaleMode scaleMode,
        int smartDeltaLevel,
        int abortAfterBytes)
    {
        List<PlannedFrame> plan =
            BuildFramePlan(
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
                    512 * 1024));

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
