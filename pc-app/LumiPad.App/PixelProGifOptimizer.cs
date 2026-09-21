using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace LumiPad.App;

internal sealed record PixelProGifOptimizationResult(
    byte[] Bytes,
    int Width,
    int Height,
    int FrameCount);

/// <summary>
/// Re-encodes PIXEL PRO GIFs for flash storage instead of blindly copying the
/// source file. The optimizer never upscales the source, caps very fast
/// animation at 25 FPS, uses one global 256-color RGB332 palette, and stores
/// only changed rectangles after the first frame.
/// </summary>
internal static class PixelProGifOptimizer
{
    private const int PanelWidth = 480;
    private const int PanelHeight = 320;
    private const int MinStoredFrameMs = 40; // 25 FPS

    private readonly record struct PlannedFrame(
        int SourceIndex,
        int DelayMs);

    public static PixelProGifOptimizationResult Optimize(
        string path,
        IReadOnlyList<int> sourceDelaysMs)
    {
        using var image = Drawing.Image.FromFile(path);
        var dimension =
            new FrameDimension(image.FrameDimensionsList[0]);

        int sourceFrameCount =
            Math.Max(1, image.GetFrameCount(dimension));

        List<PlannedFrame> plan =
            BuildFramePlan(
                sourceDelaysMs,
                sourceFrameCount);

        double scale =
            Math.Min(
                1.0,
                Math.Min(
                    PanelWidth / (double)image.Width,
                    PanelHeight / (double)image.Height));

        int width =
            Math.Max(
                1,
                (int)Math.Round(image.Width * scale));

        int height =
            Math.Max(
                1,
                (int)Math.Round(image.Height * scale));

        using var output = new MemoryStream(
            Math.Max(
                4096,
                width * height));

        WriteAscii(output, "GIF89a");
        WriteU16(output, width);
        WriteU16(output, height);

        // Global color table present, 8-bit color resolution, 256 entries.
        output.WriteByte(0xF7);
        output.WriteByte(0);
        output.WriteByte(0);

        WriteRgb332Palette(output);
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
                ToRgb332Indices(
                    frame,
                    width,
                    height);

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

        return new PixelProGifOptimizationResult(
            output.ToArray(),
            width,
            height,
            plan.Count);
    }

    private static List<PlannedFrame> BuildFramePlan(
        IReadOnlyList<int> sourceDelaysMs,
        int sourceFrameCount)
    {
        var result =
            new List<PlannedFrame>(
                Math.Min(
                    sourceFrameCount,
                    512));

        int pendingDelay = 0;
        int groupStart = 0;

        for (int i = 0; i < sourceFrameCount; i++)
        {
            if (pendingDelay == 0)
                groupStart = i;

            int delay =
                i < sourceDelaysMs.Count
                    ? sourceDelaysMs[i]
                    : 100;

            pendingDelay +=
                Math.Clamp(
                    delay,
                    10,
                    5000);

            bool flush =
                pendingDelay >= MinStoredFrameMs ||
                i == sourceFrameCount - 1;

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
                    100));
        }

        return result;
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

        using var source =
            new Drawing.Bitmap(
                image.Width,
                image.Height,
                PixelFormat.Format32bppArgb);

        using (Drawing.Graphics g =
               Drawing.Graphics.FromImage(source))
        {
            g.Clear(Drawing.Color.Black);
            g.DrawImageUnscaled(
                image,
                0,
                0);
        }

        var resized =
            new Drawing.Bitmap(
                width,
                height,
                PixelFormat.Format24bppRgb);

        using (Drawing.Graphics g =
               Drawing.Graphics.FromImage(resized))
        {
            g.Clear(Drawing.Color.Black);

            if (source.Width == width &&
                source.Height == height)
            {
                g.DrawImageUnscaled(
                    source,
                    0,
                    0);
            }
            else
            {
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
            }
        }

        return resized;
    }

    private static byte[] ToRgb332Indices(
        Drawing.Bitmap bitmap,
        int width,
        int height)
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

                    result[outputOffset + x] =
                        (byte)(
                            ((r >> 5) << 5) |
                            ((g >> 5) << 2) |
                            (b >> 6));
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return result;
    }

    private static void WriteRgb332Palette(
        Stream output)
    {
        for (int i = 0; i < 256; i++)
        {
            int r3 = (i >> 5) & 0x07;
            int g3 = (i >> 2) & 0x07;
            int b2 = i & 0x03;

            output.WriteByte(
                (byte)(
                    r3 * 255 / 7));

            output.WriteByte(
                (byte)(
                    g3 * 255 / 7));

            output.WriteByte(
                (byte)(
                    b2 * 255 / 3));
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

        // Graphics Control Extension. Disposal=1 keeps the previous canvas so
        // changed rectangles can be overlaid without storing full frames.
        output.WriteByte(0x21);
        output.WriteByte(0xF9);
        output.WriteByte(0x04);
        output.WriteByte(0x04);
        WriteU16(output, delayCs);
        output.WriteByte(0x00);
        output.WriteByte(0x00);

        // Image descriptor.
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

                    // GIF decoders add dictionary entries one emitted
                    // code later than the encoder. Grow the code width only
                    // after crossing the current limit, not when merely
                    // reaching it.
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
            (byte)(value & 0xFF));

        output.WriteByte(
            (byte)((value >> 8) &
                   0xFF));
    }
}
