using System.IO;
using System.Linq;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace LumiPad.App;

public enum ScreensaverScaleMode
{
    Fill = 0,
    Fit = 1,
    Stretch = 2,
    Tile = 3,
    Center = 4,
    Span = 5,
}

public enum ScreensaverPixelFormat
{
    Rgb332 = 0,
    Rgb565 = 1,
}

public sealed record ScreensaverAnimation(
    string FileName,
    int Width,
    int Height,
    ScreensaverPixelFormat PixelFormat,
    int FrameIntervalMs,
    IReadOnlyList<int> FrameDurationsMs,
    IReadOnlyList<byte[]> Frames);

internal sealed record RynorRawGifSource(
    string Path,
    long Length,
    int SourceWidth,
    int SourceHeight,
    int FrameCount,
    int DurationMs,
    ScreensaverScaleMode ScaleMode);

/// <summary>
/// RYNOR ONE media preparation.
///
/// GIF selection stays lightweight and never uses the old 160x86 RGB332 frame
/// pack. During upload, capable RYNOR firmware uses a PC-preprocessed 320x172
/// delta/RLE animation so the keyboard can update changed regions directly.
/// Raw GIF remains a compatibility fallback. The app only renders one 320x172
/// RGB565 preview frame for its own UI.
///
/// This follows the same asset-on-device principle visible in EezBotFun's
/// public workflow: media stays as a device asset instead of being pre-expanded
/// into a fixed set of reduced animation frames on the PC.
/// </summary>
public static class ScreensaverMediaService
{
    public const int Width = 320;
    public const int Height = 172;
    public const int StaticWidth = 320;
    public const int StaticHeight = 172;

    // RYNOR ONE reserves a 10 MiB external-flash partition for screensaver
    // media and keeps the first 4 KiB for its metadata header.
    public const long MaxRawGifBytes =
        (10L * 1024L * 1024L) - 4096L;

    // Larger source GIFs are accepted because the RYNOR upload path can
    // preprocess them on the PC into the 10 MiB delta/RLE device format.
    public const long MaxSourceGifBytes =
        64L * 1024L * 1024L;

    // GIF timing uses the GIF 1/100 s unit. Firmware preserves source delays
    // and only normalizes invalid/zero delays to 10 ms.
    public const int MaxPlaybackFps = 100;
    public const int MinFrameIntervalMs = 10;

    private static readonly ConditionalWeakTable<
        ScreensaverAnimation,
        RynorRawGifSource> RawGifSources = new();

    internal static bool TryGetRawGifSource(
        ScreensaverAnimation animation,
        out RynorRawGifSource source)
    {
        if (RawGifSources.TryGetValue(
                animation,
                out RynorRawGifSource? raw))
        {
            source = raw;
            return true;
        }

        source = null!;
        return false;
    }

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
        byte[] frame = ToRgb565(bitmap, scaleMode);

        return new ScreensaverAnimation(
            Path.GetFileName(path),
            StaticWidth,
            StaticHeight,
            ScreensaverPixelFormat.Rgb565,
            1000,
            new[] { 1000 },
            new[] { frame });
    }

    private static ScreensaverAnimation LoadGif(
        string path,
        ScreensaverScaleMode scaleMode)
    {
        var sourceFile = new FileInfo(path);

        if (!sourceFile.Exists ||
            sourceFile.Length < 13)
        {
            throw new InvalidDataException(
                "The selected GIF is empty or invalid.");
        }

        if (sourceFile.Length > MaxSourceGifBytes)
        {
            throw new InvalidDataException(
                $"RYNOR ONE accepts source GIFs up to " +
                $"{MaxSourceGifBytes / 1048576.0:0} MB. " +
                "The app optimizes them into the device animation format during upload.");
        }

        // Native RYNOR GIFs are copied byte-for-byte to the keyboard, so the
        // PC app does not need to decode every frame just to select the file.
        // Reading only the 13-byte GIF header keeps selection/startup instant
        // even for highly compressed GIFs with thousands of frames.
        Span<byte> header = stackalloc byte[13];

        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   4096,
                   FileOptions.SequentialScan))
        {
            int read = 0;

            while (read < header.Length)
            {
                int count =
                    stream.Read(
                        header.Slice(read));

                if (count <= 0)
                    break;

                read += count;
            }

            if (read != header.Length)
            {
                throw new InvalidDataException(
                    "The selected GIF header is incomplete.");
            }
        }

        bool gif87a =
            header[0] == (byte)'G' &&
            header[1] == (byte)'I' &&
            header[2] == (byte)'F' &&
            header[3] == (byte)'8' &&
            header[4] == (byte)'7' &&
            header[5] == (byte)'a';

        bool gif89a =
            header[0] == (byte)'G' &&
            header[1] == (byte)'I' &&
            header[2] == (byte)'F' &&
            header[3] == (byte)'8' &&
            header[4] == (byte)'9' &&
            header[5] == (byte)'a';

        if (!gif87a && !gif89a)
        {
            throw new InvalidDataException(
                "The selected file is not a valid GIF87a/GIF89a image.");
        }

        int sourceWidth =
            header[6] |
            (header[7] << 8);

        int sourceHeight =
            header[8] |
            (header[9] << 8);

        if (sourceWidth < 1 ||
            sourceHeight < 1 ||
            sourceWidth > 2048 ||
            sourceHeight > 2048)
        {
            throw new InvalidDataException(
                "RYNOR ONE GIF canvas must be between 1x1 and 2048x2048.");
        }

        // The real GIF is decoded on RYNOR ONE. Keep only a tiny local
        // placeholder frame so the WPF media model stays compatible without
        // spending CPU/RAM decoding the animation on the PC.
        byte[] preview =
            new byte[
                Width *
                Height *
                2];

        const int previewDelay = 100;

        var animation =
            new ScreensaverAnimation(
                Path.GetFileName(path),
                Width,
                Height,
                ScreensaverPixelFormat.Rgb565,
                previewDelay,
                new[] { previewDelay },
                new[] { preview });

        RawGifSources.Add(
            animation,
            new RynorRawGifSource(
                path,
                sourceFile.Length,
                sourceWidth,
                sourceHeight,
                0,
                0,
                scaleMode));

        return animation;
    }

    private static int[] ReadGifFrameDelaysMs(
        Drawing.Image image,
        int frameCount)
    {
        const int PropertyTagFrameDelay = 0x5100;
        var delays =
            Enumerable.Repeat(
                100,
                Math.Max(1, frameCount))
            .ToArray();

        try
        {
            PropertyItem? item =
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
                            MinFrameIntervalMs,
                            5000);
                }
            }
        }
        catch
        {
            // Some GIF encoders omit the delay property. 100 ms is the same
            // conservative fallback used by System.Drawing for such files.
        }

        return delays;
    }

    private static byte[] ToRgb565(
        Drawing.Bitmap source,
        ScreensaverScaleMode scaleMode)
    {
        using var resized = new Drawing.Bitmap(
            StaticWidth,
            StaticHeight,
            PixelFormat.Format24bppRgb);

        using (var g = Drawing.Graphics.FromImage(resized))
        {
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
                    g.DrawImage(
                        source,
                        0,
                        0,
                        StaticWidth,
                        StaticHeight);
                    break;

                case ScreensaverScaleMode.Fit:
                {
                    double scale =
                        Math.Min(
                            StaticWidth / (double)source.Width,
                            StaticHeight / (double)source.Height);
                    int drawW =
                        Math.Max(
                            1,
                            (int)Math.Round(source.Width * scale));
                    int drawH =
                        Math.Max(
                            1,
                            (int)Math.Round(source.Height * scale));
                    int dx =
                        (StaticWidth - drawW) / 2;
                    int dy =
                        (StaticHeight - drawH) / 2;
                    g.DrawImage(
                        source,
                        dx,
                        dy,
                        drawW,
                        drawH);
                    break;
                }

                case ScreensaverScaleMode.Center:
                {
                    int drawW =
                        Math.Min(
                            source.Width,
                            StaticWidth);
                    int drawH =
                        Math.Min(
                            source.Height,
                            StaticHeight);
                    int sx =
                        Math.Max(
                            0,
                            (source.Width - drawW) / 2);
                    int sy =
                        Math.Max(
                            0,
                            (source.Height - drawH) / 2);
                    int dx =
                        (StaticWidth - drawW) / 2;
                    int dy =
                        (StaticHeight - drawH) / 2;

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
                    int tileW =
                        Math.Min(
                            source.Width,
                            StaticWidth);
                    int tileH =
                        Math.Min(
                            source.Height,
                            StaticHeight);

                    using var tile =
                        new Drawing.Bitmap(
                            Math.Max(1, tileW),
                            Math.Max(1, tileH));

                    using (var tg =
                           Drawing.Graphics.FromImage(tile))
                    {
                        tg.InterpolationMode =
                            Drawing2D.InterpolationMode.HighQualityBilinear;
                        tg.DrawImage(
                            source,
                            0,
                            0,
                            tile.Width,
                            tile.Height);
                    }

                    using var brush =
                        new Drawing.TextureBrush(
                            tile,
                            Drawing2D.WrapMode.Tile);
                    g.FillRectangle(
                        brush,
                        0,
                        0,
                        StaticWidth,
                        StaticHeight);
                    break;
                }

                case ScreensaverScaleMode.Span:
                case ScreensaverScaleMode.Fill:
                default:
                {
                    double scale =
                        Math.Max(
                            StaticWidth / (double)source.Width,
                            StaticHeight / (double)source.Height);

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
                    int dx =
                        (StaticWidth - drawW) / 2;
                    int dy =
                        (StaticHeight - drawH) / 2;

                    g.DrawImage(
                        source,
                        dx,
                        dy,
                        drawW,
                        drawH);
                    break;
                }
            }
        }

        var output =
            new byte[
                StaticWidth *
                StaticHeight *
                2];

        for (int y = 0; y < StaticHeight; y++)
        {
            for (int x = 0; x < StaticWidth; x++)
            {
                Drawing.Color p =
                    resized.GetPixel(x, y);

                ushort rgb565 =
                    (ushort)(
                        ((p.R & 0xF8) << 8) |
                        ((p.G & 0xFC) << 3) |
                        (p.B >> 3));

                int o =
                    (y * StaticWidth + x) * 2;

                output[o] =
                    (byte)(rgb565 & 0xFF);
                output[o + 1] =
                    (byte)(rgb565 >> 8);
            }
        }

        return output;
    }
}
