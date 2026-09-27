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
/// GIFs are never converted into the old 160x86 RGB332 frame pack. The original
/// GIF file is kept byte-for-byte, uploaded to RYNOR ONE external flash and
/// decoded on the keyboard at playback time. The app only renders one 320x172
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

        if (sourceFile.Length > MaxRawGifBytes)
        {
            throw new InvalidDataException(
                $"RYNOR ONE stores the original GIF without recompressing it. " +
                $"Maximum GIF size is {MaxRawGifBytes / 1048576.0:0.00} MB.");
        }

        using var image = Drawing.Image.FromFile(path);

        if (image.Width < 1 ||
            image.Height < 1 ||
            image.Width > 2048 ||
            image.Height > 2048)
        {
            throw new InvalidDataException(
                "RYNOR ONE GIF canvas must be between 1x1 and 2048x2048.");
        }

        var dimension =
            new FrameDimension(image.FrameDimensionsList[0]);
        int frameCount =
            Math.Max(1, image.GetFrameCount(dimension));

        int[] sourceDelaysMs =
            ReadGifFrameDelaysMs(image, frameCount);
        int durationMs =
            Math.Max(1, sourceDelaysMs.Sum());

        image.SelectActiveFrame(dimension, 0);

        using var firstFrame = new Drawing.Bitmap(
            image.Width,
            image.Height,
            PixelFormat.Format32bppArgb);

        using (var g = Drawing.Graphics.FromImage(firstFrame))
        {
            g.Clear(Drawing.Color.Transparent);
            g.DrawImageUnscaled(image, 0, 0);
        }

        // UI preview only. This frame is never uploaded as the GIF payload.
        byte[] preview =
            ToRgb565(firstFrame, scaleMode);

        int previewDelay =
            Math.Clamp(
                sourceDelaysMs.Length > 0
                    ? sourceDelaysMs[0]
                    : 100,
                MinFrameIntervalMs,
                5000);

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
                image.Width,
                image.Height,
                frameCount,
                durationMs,
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
