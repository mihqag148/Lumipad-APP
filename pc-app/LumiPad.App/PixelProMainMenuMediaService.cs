using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace LumiPad.App;

public static class PixelProMainMenuMediaService
{
    public const int BackgroundWidth = 480;
    public const int BackgroundHeight = 320;
    public const int BackgroundMaxBytes = 96 * 1024;

    // eezBotFun-style 8-key layout uses native 96x96 icon canvases. The
    // custom PXI1 payload stores RGB565 pixels plus a transparent color key,
    // so aspect-ratio padding shows the wallpaper instead of a black JPEG box.
    public const int IconWidth = 96;
    public const int IconHeight = 96;
    public const int IconMaxBytes = 24 * 1024;
    public const ushort IconTransparent565 = 0xF81F;

    public static byte[] CreateBackgroundJpeg(
        string path,
        int blurPercent,
        int opacityPercent,
        ScreensaverScaleMode scaleMode)
    {
        ValidateStaticImage(path);

        blurPercent =
            Math.Clamp(blurPercent, 0, 100);

        opacityPercent =
            Math.Clamp(opacityPercent, 0, 100);

        if (scaleMode is not (
            ScreensaverScaleMode.Fill or
            ScreensaverScaleMode.Fit or
            ScreensaverScaleMode.Stretch))
        {
            scaleMode =
                ScreensaverScaleMode.Fill;
        }

        using var source =
            new Bitmap(path);

        using var composed =
            new Bitmap(
                BackgroundWidth,
                BackgroundHeight,
                PixelFormat.Format24bppRgb);

        using (Graphics g = Graphics.FromImage(composed))
        {
            g.Clear(Color.Black);
            ConfigureHighQuality(g);

            if (scaleMode == ScreensaverScaleMode.Stretch)
            {
                g.DrawImage(
                    source,
                    new Rectangle(
                        0,
                        0,
                        BackgroundWidth,
                        BackgroundHeight));
            }
            else if (scaleMode == ScreensaverScaleMode.Fit)
            {
                g.DrawImage(
                    source,
                    FitRect(
                        source.Width,
                        source.Height,
                        BackgroundWidth,
                        BackgroundHeight));
            }
            else
            {
                Rectangle sourceRect =
                    SourceCropRect(
                        source.Width,
                        source.Height,
                        BackgroundWidth,
                        BackgroundHeight);

                g.DrawImage(
                    source,
                    new Rectangle(
                        0,
                        0,
                        BackgroundWidth,
                        BackgroundHeight),
                    sourceRect,
                    GraphicsUnit.Pixel);
            }
        }

        using Bitmap blurred =
            CreateBlurredBitmap(
                composed,
                blurPercent);

        using var output =
            new Bitmap(
                BackgroundWidth,
                BackgroundHeight,
                PixelFormat.Format24bppRgb);

        using (Graphics g = Graphics.FromImage(output))
        {
            g.Clear(Color.Black);

            float alpha =
                opacityPercent / 100f;

            using var attributes =
                new ImageAttributes();

            attributes.SetColorMatrix(
                new ColorMatrix(
                    new[]
                    {
                        new[] { 1f, 0f, 0f, 0f, 0f },
                        new[] { 0f, 1f, 0f, 0f, 0f },
                        new[] { 0f, 0f, 1f, 0f, 0f },
                        new[] { 0f, 0f, 0f, alpha, 0f },
                        new[] { 0f, 0f, 0f, 0f, 1f }
                    }));

            g.DrawImage(
                blurred,
                new Rectangle(
                    0,
                    0,
                    BackgroundWidth,
                    BackgroundHeight),
                0,
                0,
                BackgroundWidth,
                BackgroundHeight,
                GraphicsUnit.Pixel,
                attributes);
        }

        return EncodeJpegWithinLimit(
            output,
            BackgroundMaxBytes,
            [90, 84, 78, 72, 66, 60, 54, 48],
            "Main-menu background cannot be reduced below 96 KiB.");
    }

    private static GraphicsPath RoundedRectPath(
        Rectangle bounds,
        int radius)
    {
        int diameter =
            Math.Max(
                2,
                radius * 2);

        var path =
            new GraphicsPath();

        path.AddArc(
            bounds.Left,
            bounds.Top,
            diameter,
            diameter,
            180,
            90);

        path.AddArc(
            bounds.Right - diameter,
            bounds.Top,
            diameter,
            diameter,
            270,
            90);

        path.AddArc(
            bounds.Right - diameter,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);

        path.AddArc(
            bounds.Left,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            90,
            90);

        path.CloseFigure();

        return path;
    }

    private static Bitmap CreateNormalizedAppIcon(
        string path)
    {
        ValidateStaticImage(path);

        using var source =
            new Bitmap(path);

        var output =
            new Bitmap(
                IconWidth,
                IconHeight,
                PixelFormat.Format32bppArgb);

        using Graphics g =
            Graphics.FromImage(
                output);

        g.Clear(
            Color.Transparent);

        ConfigureHighQuality(g);

        Rectangle visible =
            FindVisibleBounds(
                source);

        // iOS/macOS-style normalization: every app is optically centered
        // inside the same 80x80 rounded-square envelope. We do not add a
        // black frame or force a background; transparent source artwork stays
        // transparent while oversized square icons get softly rounded corners.
        const int visualSize = 68;
        const int inset =
            (IconWidth -
             visualSize) /
            2;

        Rectangle envelope =
            new(
                inset,
                inset,
                visualSize,
                visualSize);

        Rectangle fit =
            FitRect(
                visible.Width,
                visible.Height,
                visualSize,
                visualSize);

        fit.Offset(
            inset,
            inset);

        using GraphicsPath clip =
            RoundedRectPath(
                envelope,
                15);

        GraphicsState state =
            g.Save();

        g.SetClip(
            clip);

        g.DrawImage(
            source,
            fit,
            visible,
            GraphicsUnit.Pixel);

        g.Restore(
            state);

        return output;
    }

    public static byte[] CreateIconPreviewPng(
        string path)
    {
        using Bitmap normalized =
            CreateNormalizedAppIcon(
                path);

        using var output =
            new MemoryStream();

        normalized.Save(
            output,
            ImageFormat.Png);

        return output.ToArray();
    }

    public static byte[] CreateIconAsset(string path)
    {
        using Bitmap output =
            CreateNormalizedAppIcon(
                path);

        using var stream =
            new MemoryStream(
                8 +
                IconWidth *
                IconHeight *
                2);

        using var writer =
            new BinaryWriter(stream);

        writer.Write(
            new byte[]
            {
                (byte)'P',
                (byte)'X',
                (byte)'I',
                (byte)'1'
            });

        writer.Write(
            (ushort)IconWidth);

        writer.Write(
            (ushort)IconHeight);

        for (int y = 0; y < IconHeight; y++)
        {
            for (int x = 0; x < IconWidth; x++)
            {
                Color pixel =
                    output.GetPixel(x, y);

                ushort rgb565;

                if (pixel.A <= 16)
                {
                    rgb565 =
                        IconTransparent565;
                }
                else
                {
                    int r =
                        (pixel.R * 31 + 127) /
                        255;

                    int green =
                        (pixel.G * 63 + 127) /
                        255;

                    int b =
                        (pixel.B * 31 + 127) /
                        255;

                    rgb565 =
                        (ushort)(
                            (r << 11) |
                            (green << 5) |
                            b);

                    if (rgb565 == IconTransparent565)
                        rgb565 = 0xF81E;
                }

                writer.Write(
                    rgb565);
            }
        }

        return stream.ToArray();
    }

    private static byte[] EncodeJpegWithinLimit(
        Bitmap image,
        int maxBytes,
        long[] qualities,
        string failureMessage)
    {
        ImageCodecInfo codec =
            ImageCodecInfo.GetImageEncoders()
                .First(
                    x =>
                        x.FormatID ==
                        ImageFormat.Jpeg.Guid);

        foreach (long quality in qualities)
        {
            using var stream =
                new MemoryStream();

            using var parameters =
                new EncoderParameters(1);

            parameters.Param[0] =
                new EncoderParameter(
                    Encoder.Quality,
                    quality);

            image.Save(
                stream,
                codec,
                parameters);

            if (stream.Length <= maxBytes)
                return stream.ToArray();
        }

        throw new InvalidOperationException(
            failureMessage);
    }

    private static Bitmap CreateBlurredBitmap(
        Bitmap source,
        int blurPercent)
    {
        if (blurPercent <= 0)
            return new Bitmap(source);

        double amount =
            Math.Clamp(
                blurPercent / 100.0,
                0.0,
                1.0);

        int divisor =
            2 +
            (int)Math.Round(
                amount * 14.0);

        int smallWidth =
            Math.Max(
                1,
                source.Width / divisor);

        int smallHeight =
            Math.Max(
                1,
                source.Height / divisor);

        using var small =
            new Bitmap(
                smallWidth,
                smallHeight,
                PixelFormat.Format24bppRgb);

        using (Graphics g = Graphics.FromImage(small))
        {
            g.Clear(Color.Black);
            g.CompositingQuality =
                CompositingQuality.HighQuality;
            g.InterpolationMode =
                InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode =
                PixelOffsetMode.HighQuality;

            g.DrawImage(
                source,
                new Rectangle(
                    0,
                    0,
                    smallWidth,
                    smallHeight));
        }

        var result =
            new Bitmap(
                source.Width,
                source.Height,
                PixelFormat.Format24bppRgb);

        using (Graphics g = Graphics.FromImage(result))
        {
            g.Clear(Color.Black);
            ConfigureHighQuality(g);
            g.DrawImage(
                small,
                new Rectangle(
                    0,
                    0,
                    source.Width,
                    source.Height));
        }

        return result;
    }

    private static void ConfigureHighQuality(
        Graphics graphics)
    {
        graphics.CompositingQuality =
            CompositingQuality.HighQuality;
        graphics.InterpolationMode =
            InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode =
            PixelOffsetMode.HighQuality;
        graphics.SmoothingMode =
            SmoothingMode.HighQuality;
    }

    private static Rectangle FindVisibleBounds(
        Bitmap source)
    {
        bool hasAlpha =
            Image.IsAlphaPixelFormat(
                source.PixelFormat);

        if (!hasAlpha)
        {
            return new Rectangle(
                0,
                0,
                source.Width,
                source.Height);
        }

        int left = source.Width;
        int top = source.Height;
        int right = -1;
        int bottom = -1;

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                if (source.GetPixel(x, y).A <= 8)
                    continue;

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top)
        {
            return new Rectangle(
                0,
                0,
                source.Width,
                source.Height);
        }

        return Rectangle.FromLTRB(
            left,
            top,
            right + 1,
            bottom + 1);
    }

    private static void FillRoundRectCompat(
        this Graphics graphics,
        Brush brush,
        RectangleF rect,
        float radius)
    {
        using var path =
            new GraphicsPath();

        float diameter =
            Math.Max(
                2f,
                radius * 2f);

        path.AddArc(
            rect.Left,
            rect.Top,
            diameter,
            diameter,
            180,
            90);

        path.AddArc(
            rect.Right - diameter,
            rect.Top,
            diameter,
            diameter,
            270,
            90);

        path.AddArc(
            rect.Right - diameter,
            rect.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);

        path.AddArc(
            rect.Left,
            rect.Bottom - diameter,
            diameter,
            diameter,
            90,
            90);

        path.CloseFigure();

        graphics.FillPath(
            brush,
            path);
    }

    public static byte[] CreateDockIconPreviewPng(
        int hostOs,
        int slot)
    {
        hostOs =
            Math.Clamp(
                hostOs,
                1,
                3);

        slot =
            Math.Clamp(
                slot,
                0,
                3);

        const int size = 64;

        using var output =
            new Bitmap(
                size,
                size,
                PixelFormat.Format32bppArgb);

        using Graphics g =
            Graphics.FromImage(
                output);

        g.Clear(
            Color.Transparent);

        ConfigureHighQuality(g);

        static SolidBrush Brush(
            int r,
            int gg,
            int b) =>
            new(
                Color.FromArgb(
                    r,
                    gg,
                    b));

        static Pen PenOf(
            int r,
            int gg,
            int b,
            float width = 2f) =>
            new(
                Color.FromArgb(
                    r,
                    gg,
                    b),
                width);

        void DrawGear(
            Color fill,
            Color stroke)
        {
            const int cx = 32;
            const int cy = 32;

            using var tooth =
                new SolidBrush(fill);

            for (int i = 0; i < 8; i++)
            {
                GraphicsState state =
                    g.Save();

                g.TranslateTransform(
                    cx,
                    cy);

                g.RotateTransform(
                    i * 45f);

                g.FillRoundRectCompat(
                    tooth,
                    new RectangleF(
                        -4,
                        -27,
                        8,
                        12),
                    3);

                g.Restore(
                    state);
            }

            using var body =
                new SolidBrush(fill);

            g.FillEllipse(
                body,
                13,
                13,
                38,
                38);

            using var hole =
                new SolidBrush(
                    Color.Transparent);

            using var centerBrush =
                new SolidBrush(
                    Color.FromArgb(
                        22,
                        24,
                        29));

            g.FillEllipse(
                centerBrush,
                24,
                24,
                16,
                16);

            using var outline =
                new Pen(
                    stroke,
                    2f);

            g.DrawEllipse(
                outline,
                13,
                13,
                38,
                38);
        }

        if (hostOs == 1)
        {
            if (slot == 0)
            {
                using var blue =
                    Brush(
                        0,
                        164,
                        239);

                g.FillRectangle(
                    blue,
                    10,
                    10,
                    19,
                    19);

                g.FillRectangle(
                    blue,
                    35,
                    10,
                    19,
                    19);

                g.FillRectangle(
                    blue,
                    10,
                    35,
                    19,
                    19);

                g.FillRectangle(
                    blue,
                    35,
                    35,
                    19,
                    19);
            }
            else if (slot == 1)
            {
                using var tab =
                    Brush(
                        255,
                        194,
                        45);

                using var body =
                    Brush(
                        255,
                        209,
                        75);

                using var accent =
                    Brush(
                        70,
                        145,
                        255);

                g.FillRoundRectCompat(
                    tab,
                    new RectangleF(
                        8,
                        15,
                        25,
                        14),
                    5);

                g.FillRoundRectCompat(
                    body,
                    new RectangleF(
                        7,
                        22,
                        50,
                        31),
                    7);

                g.FillRoundRectCompat(
                    accent,
                    new RectangleF(
                        31,
                        19,
                        21,
                        7),
                    3);
            }
            else if (slot == 2)
            {
                using var blue =
                    Brush(
                        0,
                        120,
                        212);

                using var teal =
                    Brush(
                        18,
                        190,
                        175);

                using var dark =
                    Brush(
                        0,
                        70,
                        135);

                g.FillEllipse(
                    blue,
                    7,
                    7,
                    50,
                    50);

                g.FillPie(
                    teal,
                    7,
                    7,
                    50,
                    50,
                    165,
                    205);

                g.FillEllipse(
                    dark,
                    20,
                    20,
                    29,
                    24);

                g.FillPie(
                    teal,
                    13,
                    18,
                    38,
                    31,
                    205,
                    130);
            }
            else
            {
                DrawGear(
                    Color.FromArgb(
                        128,
                        137,
                        151),
                    Color.FromArgb(
                        220,
                        225,
                        232));
            }
        }
        else if (hostOs == 2)
        {
            if (slot == 0)
            {
                Color[] colors =
                [
                    Color.FromArgb(86, 149, 255),
                    Color.FromArgb(142, 83, 255),
                    Color.FromArgb(255, 91, 109),
                    Color.FromArgb(43, 199, 140),
                    Color.FromArgb(255, 181, 49),
                    Color.FromArgb(85, 205, 255),
                    Color.FromArgb(255, 105, 180),
                    Color.FromArgb(125, 125, 235),
                    Color.FromArgb(96, 220, 120)
                ];

                for (int row = 0; row < 3; row++)
                {
                    for (int col = 0; col < 3; col++)
                    {
                        using var brush =
                            new SolidBrush(
                                colors[row * 3 + col]);

                        g.FillRoundRectCompat(
                            brush,
                            new RectangleF(
                                10 + col * 16,
                                10 + row * 16,
                                12,
                                12),
                            4);
                    }
                }
            }
            else if (slot == 1)
            {
                using var light =
                    Brush(
                        95,
                        186,
                        255);

                using var deep =
                    Brush(
                        47,
                        138,
                        235);

                using var ink =
                    Brush(
                        20,
                        65,
                        105);

                g.FillRoundRectCompat(
                    light,
                    new RectangleF(
                        7,
                        7,
                        50,
                        50),
                    11);

                g.FillRectangle(
                    deep,
                    32,
                    7,
                    25,
                    50);

                using var blackPen =
                    PenOf(
                        15,
                        60,
                        100,
                        2f);

                g.DrawLine(
                    blackPen,
                    32,
                    11,
                    32,
                    50);

                g.FillEllipse(
                    ink,
                    20,
                    25,
                    3,
                    5);

                g.FillEllipse(
                    ink,
                    41,
                    25,
                    3,
                    5);

                g.DrawArc(
                    blackPen,
                    20,
                    29,
                    24,
                    15,
                    10,
                    160);
            }
            else if (slot == 2)
            {
                using var blue =
                    Brush(
                        52,
                        161,
                        255);

                using var white =
                    Brush(
                        245,
                        250,
                        255);

                using var red =
                    Brush(
                        242,
                        74,
                        72);

                g.FillEllipse(
                    blue,
                    7,
                    7,
                    50,
                    50);

                g.FillEllipse(
                    white,
                    14,
                    14,
                    36,
                    36);

                g.FillEllipse(
                    blue,
                    17,
                    17,
                    30,
                    30);

                PointF[] needle =
                [
                    new(32, 13),
                    new(36, 33),
                    new(28, 33)
                ];

                g.FillPolygon(
                    red,
                    needle);

                using var center =
                    Brush(
                        250,
                        250,
                        250);

                g.FillEllipse(
                    center,
                    29,
                    29,
                    6,
                    6);
            }
            else
            {
                DrawGear(
                    Color.FromArgb(
                        160,
                        164,
                        172),
                    Color.FromArgb(
                        236,
                        238,
                        242));
            }
        }
        else
        {
            if (slot == 0)
            {
                using var white =
                    Brush(
                        246,
                        246,
                        246);

                for (int row = 0; row < 3; row++)
                {
                    for (int col = 0; col < 3; col++)
                    {
                        g.FillEllipse(
                            white,
                            13 + col * 15,
                            13 + row * 15,
                            7,
                            7);
                    }
                }
            }
            else if (slot == 1)
            {
                using var tab =
                    Brush(
                        92,
                        155,
                        255);

                using var body =
                    Brush(
                        68,
                        129,
                        224);

                g.FillRoundRectCompat(
                    tab,
                    new RectangleF(
                        8,
                        15,
                        26,
                        14),
                    5);

                g.FillRoundRectCompat(
                    body,
                    new RectangleF(
                        7,
                        22,
                        50,
                        31),
                    7);
            }
            else if (slot == 2)
            {
                using var purple =
                    Brush(
                        96,
                        61,
                        165);

                using var orange =
                    Brush(
                        255,
                        122,
                        35);

                using var blue =
                    Brush(
                        54,
                        121,
                        205);

                g.FillEllipse(
                    purple,
                    7,
                    7,
                    50,
                    50);

                g.FillPie(
                    orange,
                    7,
                    7,
                    50,
                    50,
                    210,
                    235);

                g.FillEllipse(
                    blue,
                    20,
                    20,
                    25,
                    25);
            }
            else
            {
                DrawGear(
                    Color.FromArgb(
                        132,
                        139,
                        149),
                    Color.FromArgb(
                        224,
                        227,
                        232));
            }
        }

        using var stream =
            new MemoryStream();

        output.Save(
            stream,
            ImageFormat.Png);

        return stream.ToArray();
    }

    public static void ValidateStaticImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path))
        {
            throw new FileNotFoundException(
                "Image file was not found.",
                path);
        }

        string ext =
            Path.GetExtension(path)
                .ToLowerInvariant();

        if (ext == ".gif")
        {
            throw new InvalidOperationException(
                "Main-menu background and icons must be static images. GIF is not allowed.");
        }

        if (ext is not ".png" and
            not ".jpg" and
            not ".jpeg" and
            not ".bmp")
        {
            throw new NotSupportedException(
                "Choose a PNG, JPG/JPEG, or BMP image.");
        }
    }

    private static Rectangle SourceCropRect(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        double scale =
            Math.Max(
                targetWidth / (double)sourceWidth,
                targetHeight / (double)sourceHeight);

        int cropWidth =
            Math.Max(
                1,
                (int)Math.Round(
                    targetWidth / scale));

        int cropHeight =
            Math.Max(
                1,
                (int)Math.Round(
                    targetHeight / scale));

        return new Rectangle(
            Math.Max(
                0,
                (sourceWidth - cropWidth) / 2),
            Math.Max(
                0,
                (sourceHeight - cropHeight) / 2),
            Math.Min(
                sourceWidth,
                cropWidth),
            Math.Min(
                sourceHeight,
                cropHeight));
    }

    private static Rectangle FitRect(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        double scale =
            Math.Min(
                targetWidth / (double)sourceWidth,
                targetHeight / (double)sourceHeight);

        int width =
            Math.Max(
                1,
                (int)Math.Round(
                    sourceWidth * scale));

        int height =
            Math.Max(
                1,
                (int)Math.Round(
                    sourceHeight * scale));

        return new Rectangle(
            (targetWidth - width) / 2,
            (targetHeight - height) / 2,
            width,
            height);
    }
}
