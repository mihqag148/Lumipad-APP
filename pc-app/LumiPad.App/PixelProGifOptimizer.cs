using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;

namespace LumiPad.App;

internal sealed record PixelProGifOptimizationResult(
    byte[] Bytes,
    int Width,
    int Height,
    int FrameCount,
    int StoredFps,
    int DurationMs);

/// <summary>
/// PIXEL PRO-only GIF optimizer.
/// Resolution is never reduced. The encoder keeps a full 256-color adaptive
/// global palette and only reduces temporal density (60..20 FPS) when required
/// to meet the configured storage target.
/// </summary>
internal static class PixelProGifOptimizer
{
    private readonly record struct PlannedFrame(
        int SourceIndex,
        int DelayMs);

    public static PixelProGifOptimizationResult Optimize(
        string path,
        IReadOnlyList<int> sourceDelaysMs,
        int requestedMaxFps,
        int maxDurationMs,
        int targetBytes)
    {
        requestedMaxFps = Math.Clamp(requestedMaxFps, 20, 60);
        maxDurationMs = Math.Clamp(maxDurationMs, 1000, 60000);
        targetBytes = Math.Max(64 * 1024, targetBytes);

        using var image = Drawing.Image.FromFile(path);
        var dimension = new FrameDimension(image.FrameDimensionsList[0]);

        int sourceFrameCount =
            Math.Max(1, image.GetFrameCount(dimension));

        int width = image.Width;
        int height = image.Height;

        List<int> candidates =
            new[] { requestedMaxFps, 50, 40, 30, 25, 20 }
                .Where(x => x <= requestedMaxFps && x >= 20)
                .Distinct()
                .OrderByDescending(x => x)
                .ToList();

        if (!candidates.Contains(20))
            candidates.Add(20);

        List<PlannedFrame> palettePlan =
            BuildFramePlan(
                sourceDelaysMs,
                sourceFrameCount,
                candidates[0],
                maxDurationMs);

        Drawing.Color[] palette =
            BuildAdaptivePalette(
                image,
                dimension,
                palettePlan,
                width,
                height);

        byte[] paletteLookup =
            BuildPaletteLookup(palette);

        PixelProGifOptimizationResult? last = null;

        foreach (int fps in candidates)
        {
            List<PlannedFrame> plan =
                BuildFramePlan(
                    sourceDelaysMs,
                    sourceFrameCount,
                    fps,
                    maxDurationMs);

            byte[] bytes =
                Encode(
                    image,
                    dimension,
                    plan,
                    width,
                    height,
                    palette,
                    paletteLookup);

            int durationMs =
                Math.Max(
                    1,
                    plan.Sum(x => x.DelayMs));

            int storedFps =
                Math.Clamp(
                    (int)Math.Round(
                        plan.Count * 1000.0 /
                        durationMs),
                    1,
                    60);

            last =
                new PixelProGifOptimizationResult(
                    bytes,
                    width,
                    height,
                    plan.Count,
                    storedFps,
                    durationMs);

            if (bytes.Length <= targetBytes)
                return last;
        }

        return last ??
            throw new InvalidOperationException(
                "PIXEL PRO GIF optimization produced no frames.");
    }

    private static List<PlannedFrame> BuildFramePlan(
        IReadOnlyList<int> sourceDelaysMs,
        int sourceFrameCount,
        int maxFps,
        int maxDurationMs)
    {
        int minFrameMs =
            Math.Max(
                17,
                (int)Math.Ceiling(
                    1000.0 /
                    Math.Clamp(maxFps, 20, 60)));

        var result =
            new List<PlannedFrame>(
                Math.Min(
                    sourceFrameCount,
                    1024));

        int pendingDelay = 0;
        int groupStart = 0;
        int elapsed = 0;

        for (int i = 0;
             i < sourceFrameCount &&
             elapsed < maxDurationMs;
             i++)
        {
            if (pendingDelay == 0)
                groupStart = i;

            int sourceDelay =
                i < sourceDelaysMs.Count
                    ? sourceDelaysMs[i]
                    : 100;

            sourceDelay =
                Math.Clamp(
                    sourceDelay,
                    10,
                    5000);

            int remaining =
                maxDurationMs -
                elapsed;

            int accepted =
                Math.Min(
                    sourceDelay,
                    remaining);

            pendingDelay += accepted;
            elapsed += accepted;

            bool flush =
                pendingDelay >= minFrameMs ||
                i == sourceFrameCount - 1 ||
                elapsed >= maxDurationMs;

            if (!flush)
                continue;

            result.Add(
                new PlannedFrame(
                    groupStart,
                    pendingDelay));

            pendingDelay = 0;
        }

        if (result.Count == 0)
        {
            result.Add(
                new PlannedFrame(
                    0,
                    Math.Min(100, maxDurationMs)));
        }

        return result;
    }

    private static byte[] Encode(
        Drawing.Image image,
        FrameDimension dimension,
        IReadOnlyList<PlannedFrame> plan,
        int width,
        int height,
        IReadOnlyList<Drawing.Color> palette,
        byte[] paletteLookup)
    {
        using var output =
            new MemoryStream(
                Math.Max(
                    4096,
                    Math.Min(
                        width * height,
                        1024 * 1024)));

        WriteAscii(output, "GIF89a");
        WriteU16(output, width);
        WriteU16(output, height);

        // GCT present, 8-bit color resolution, 256 entries.
        output.WriteByte(0xF7);
        output.WriteByte(0);
        output.WriteByte(0);

        WritePalette(
            output,
            palette);

        WriteLoopExtension(output);

        byte[]? previous = null;

        foreach (PlannedFrame planned in plan)
        {
            using Drawing.Bitmap frame =
                RenderFrame(
                    image,
                    dimension,
                    planned.SourceIndex,
                    width,
                    height);

            byte[] indexed =
                ToPaletteIndices(
                    frame,
                    width,
                    height,
                    paletteLookup);

            WriteFrame(
                output,
                indexed,
                previous,
                width,
                height,
                planned.DelayMs);

            previous = indexed;
        }

        output.WriteByte(0x3B);
        return output.ToArray();
    }

    private static Drawing.Bitmap RenderFrame(
        Drawing.Image image,
        FrameDimension dimension,
        int sourceIndex,
        int width,
        int height)
    {
        image.SelectActiveFrame(
            dimension,
            sourceIndex);

        var frame =
            new Drawing.Bitmap(
                width,
                height,
                PixelFormat.Format24bppRgb);

        using Drawing.Graphics g =
            Drawing.Graphics.FromImage(frame);

        g.Clear(Drawing.Color.Black);
        g.DrawImageUnscaled(
            image,
            0,
            0);

        return frame;
    }

    private static Drawing.Color[] BuildAdaptivePalette(
        Drawing.Image image,
        FrameDimension dimension,
        IReadOnlyList<PlannedFrame> plan,
        int width,
        int height)
    {
        var histogram =
            new int[32 * 32 * 32];

        int sampleFrames =
            Math.Min(
                24,
                plan.Count);

        var chosen =
            new HashSet<int>();

        for (int n = 0; n < sampleFrames; n++)
        {
            int planIndex =
                sampleFrames == 1
                    ? 0
                    : (int)Math.Round(
                        n *
                        (plan.Count - 1) /
                        (double)(sampleFrames - 1));

            if (!chosen.Add(planIndex))
                continue;

            using Drawing.Bitmap frame =
                RenderFrame(
                    image,
                    dimension,
                    plan[planIndex].SourceIndex,
                    width,
                    height);

            long totalPixels =
                (long)width *
                height *
                Math.Max(1, sampleFrames);

            int step =
                Math.Max(
                    1,
                    (int)Math.Sqrt(
                        totalPixels /
                        120000.0));

            for (int y = 0; y < height; y += step)
            {
                for (int x = 0; x < width; x += step)
                {
                    Drawing.Color p =
                        frame.GetPixel(x, y);

                    int bin =
                        ((p.R >> 3) << 10) |
                        ((p.G >> 3) << 5) |
                        (p.B >> 3);

                    histogram[bin]++;
                }
            }
        }

        int[] best =
            Enumerable
                .Range(0, histogram.Length)
                .Where(i => histogram[i] > 0)
                .OrderByDescending(i => histogram[i])
                .Take(256)
                .ToArray();

        var palette =
            new Drawing.Color[256];

        for (int i = 0; i < palette.Length; i++)
        {
            if (i >= best.Length)
            {
                palette[i] =
                    i == 0
                        ? Drawing.Color.Black
                        : palette[Math.Max(0, best.Length - 1)];
                continue;
            }

            int bin = best[i];

            int r5 = (bin >> 10) & 31;
            int g5 = (bin >> 5) & 31;
            int b5 = bin & 31;

            palette[i] =
                Drawing.Color.FromArgb(
                    Math.Min(255, (r5 << 3) | 4),
                    Math.Min(255, (g5 << 3) | 4),
                    Math.Min(255, (b5 << 3) | 4));
        }

        return palette;
    }

    private static byte[] BuildPaletteLookup(
        IReadOnlyList<Drawing.Color> palette)
    {
        var lookup =
            new byte[32 * 32 * 32];

        for (int bin = 0; bin < lookup.Length; bin++)
        {
            int r =
                Math.Min(
                    255,
                    (((bin >> 10) & 31) << 3) | 4);

            int g =
                Math.Min(
                    255,
                    (((bin >> 5) & 31) << 3) | 4);

            int b =
                Math.Min(
                    255,
                    ((bin & 31) << 3) | 4);

            int bestIndex = 0;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < palette.Count; i++)
            {
                Drawing.Color p =
                    palette[i];

                int dr = r - p.R;
                int dg = g - p.G;
                int db = b - p.B;

                int distance =
                    dr * dr +
                    dg * dg +
                    db * db;

                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestIndex = i;

                if (distance == 0)
                    break;
            }

            lookup[bin] =
                (byte)bestIndex;
        }

        return lookup;
    }

    private static byte[] ToPaletteIndices(
        Drawing.Bitmap bitmap,
        int width,
        int height,
        byte[] paletteLookup)
    {
        var result =
            new byte[width * height];

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
            int stride = data.Stride;
            int rowBytes = Math.Abs(stride);
            var row = new byte[rowBytes];

            for (int y = 0; y < height; y++)
            {
                IntPtr rowPtr =
                    IntPtr.Add(
                        data.Scan0,
                        y * stride);

                Marshal.Copy(
                    rowPtr,
                    row,
                    0,
                    rowBytes);

                int outputOffset =
                    y * width;

                for (int x = 0; x < width; x++)
                {
                    int p = x * 3;
                    byte b = row[p];
                    byte g = row[p + 1];
                    byte r = row[p + 2];

                    int bin =
                        ((r >> 3) << 10) |
                        ((g >> 3) << 5) |
                        (b >> 3);

                    result[outputOffset + x] =
                        paletteLookup[bin];
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return result;
    }

    private static void WritePalette(
        Stream output,
        IReadOnlyList<Drawing.Color> palette)
    {
        for (int i = 0; i < 256; i++)
        {
            Drawing.Color color =
                i < palette.Count
                    ? palette[i]
                    : Drawing.Color.Black;

            output.WriteByte(color.R);
            output.WriteByte(color.G);
            output.WriteByte(color.B);
        }
    }

    private static void WriteLoopExtension(
        Stream output)
    {
        output.WriteByte(0x21);
        output.WriteByte(0xFF);
        output.WriteByte(0x0B);
        WriteAscii(output, "NETSCAPE2.0");
        output.WriteByte(0x03);
        output.WriteByte(0x01);
        WriteU16(output, 0);
        output.WriteByte(0x00);
    }

    private static void WriteFrame(
        Stream output,
        byte[] current,
        byte[]? previous,
        int width,
        int height,
        int delayMs)
    {
        int left = 0;
        int top = 0;
        int right = width - 1;
        int bottom = height - 1;

        if (previous is not null &&
            previous.Length == current.Length)
        {
            left = width;
            top = height;
            right = -1;
            bottom = -1;

            for (int y = 0; y < height; y++)
            {
                int row = y * width;

                for (int x = 0; x < width; x++)
                {
                    int index = row + x;

                    if (current[index] ==
                        previous[index])
                    {
                        continue;
                    }

                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }

            if (right < left ||
                bottom < top)
            {
                left = 0;
                top = 0;
                right = 0;
                bottom = 0;
            }
        }

        int frameWidth =
            right - left + 1;

        int frameHeight =
            bottom - top + 1;

        ushort delayCs =
            (ushort)Math.Clamp(
                (delayMs + 5) / 10,
                1,
                ushort.MaxValue);

        output.WriteByte(0x21);
        output.WriteByte(0xF9);
        output.WriteByte(0x04);
        output.WriteByte(0x04);
        WriteU16(output, delayCs);
        output.WriteByte(0x00);
        output.WriteByte(0x00);

        output.WriteByte(0x2C);
        WriteU16(output, left);
        WriteU16(output, top);
        WriteU16(output, frameWidth);
        WriteU16(output, frameHeight);
        output.WriteByte(0x00);

        var rectangle =
            new byte[frameWidth * frameHeight];

        int offset = 0;

        for (int y = top; y <= bottom; y++)
        {
            Buffer.BlockCopy(
                current,
                y * width + left,
                rectangle,
                offset,
                frameWidth);

            offset += frameWidth;
        }

        WriteLzwImageData(
            output,
            rectangle);
    }

    private static void WriteLzwImageData(
        Stream output,
        byte[] indices)
    {
        const int minimumCodeSize = 8;
        const int clearCode = 1 << minimumCodeSize;
        const int endCode = clearCode + 1;

        output.WriteByte(minimumCodeSize);

        var compressed =
            new List<byte>(
                Math.Max(
                    64,
                    indices.Length / 2));

        int bitBuffer = 0;
        int bitCount = 0;

        void WriteCode(
            int code,
            int codeSize)
        {
            bitBuffer |=
                code << bitCount;

            bitCount += codeSize;

            while (bitCount >= 8)
            {
                compressed.Add(
                    (byte)(bitBuffer & 0xFF));

                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        var dictionary =
            new Dictionary<int, int>(
                4096);

        int codeSize = minimumCodeSize + 1;
        int nextCode = endCode + 1;

        void ResetDictionary()
        {
            dictionary.Clear();
            codeSize =
                minimumCodeSize + 1;
            nextCode =
                endCode + 1;
        }

        WriteCode(
            clearCode,
            codeSize);

        if (indices.Length > 0)
        {
            int prefix =
                indices[0];

            for (int i = 1;
                 i < indices.Length;
                 i++)
            {
                int value =
                    indices[i];

                int key =
                    (prefix << 8) |
                    value;

                if (dictionary.TryGetValue(
                        key,
                        out int found))
                {
                    prefix = found;
                    continue;
                }

                WriteCode(
                    prefix,
                    codeSize);

                if (nextCode < 4096)
                {
                    dictionary[key] =
                        nextCode++;

                    if (nextCode >
                            (1 << codeSize) &&
                        codeSize < 12)
                    {
                        codeSize++;
                    }
                }
                else
                {
                    WriteCode(
                        clearCode,
                        codeSize);

                    ResetDictionary();
                }

                prefix = value;
            }

            WriteCode(
                prefix,
                codeSize);
        }

        WriteCode(
            endCode,
            codeSize);

        if (bitCount > 0)
        {
            compressed.Add(
                (byte)(bitBuffer & 0xFF));
        }

        int position = 0;

        while (position <
               compressed.Count)
        {
            int block =
                Math.Min(
                    255,
                    compressed.Count -
                    position);

            output.WriteByte(
                (byte)block);

            for (int i = 0;
                 i < block;
                 i++)
            {
                output.WriteByte(
                    compressed[position + i]);
            }

            position += block;
        }

        output.WriteByte(0x00);
    }

    private static void WriteAscii(
        Stream output,
        string value)
    {
        foreach (char ch in value)
            output.WriteByte((byte)ch);
    }

    private static void WriteU16(
        Stream output,
        int value)
    {
        output.WriteByte(
            (byte)(value & 0xFF));

        output.WriteByte(
            (byte)((value >> 8) &
                   0xFF));
    }
}
