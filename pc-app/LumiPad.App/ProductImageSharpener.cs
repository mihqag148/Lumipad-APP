using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace LumiPad.App;

internal static class ProductImageSharpener
{
    private const int TargetLongEdge = 640;
    private const double SharpenAmount = 0.34;

    public static ImageSource Create(ImageSource source)
    {
        if (source is not BitmapSource bitmapSource)
            return source;

        using var input = new MemoryStream();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(
            BitmapFrame.Create(bitmapSource));
        encoder.Save(input);
        input.Position = 0;

        using var original =
            new Drawing.Bitmap(input);

        double scale =
            Math.Max(
                1.0,
                TargetLongEdge /
                (double)Math.Max(
                    original.Width,
                    original.Height));

        int width =
            Math.Max(
                1,
                (int)Math.Round(
                    original.Width * scale));

        int height =
            Math.Max(
                1,
                (int)Math.Round(
                    original.Height * scale));

        using var scaled =
            new Drawing.Bitmap(
                width,
                height,
                PixelFormat.Format32bppArgb);

        using (Drawing.Graphics g =
               Drawing.Graphics.FromImage(scaled))
        {
            g.CompositingMode =
                System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.CompositingQuality =
                System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.InterpolationMode =
                InterpolationMode.HighQualityBicubic;
            g.SmoothingMode =
                SmoothingMode.HighQuality;
            g.PixelOffsetMode =
                PixelOffsetMode.HighQuality;

            g.DrawImage(
                original,
                new Drawing.Rectangle(
                    0,
                    0,
                    width,
                    height),
                0,
                0,
                original.Width,
                original.Height,
                Drawing.GraphicsUnit.Pixel);
        }

        SharpenInPlace(
            scaled,
            SharpenAmount);

        using var output =
            new MemoryStream();

        scaled.Save(
            output,
            ImageFormat.Png);

        output.Position = 0;

        var result =
            new BitmapImage();

        result.BeginInit();
        result.CacheOption =
            BitmapCacheOption.OnLoad;
        result.CreateOptions =
            BitmapCreateOptions.PreservePixelFormat;
        result.StreamSource =
            output;
        result.EndInit();
        result.Freeze();

        return result;
    }

    private static void SharpenInPlace(
        Drawing.Bitmap bitmap,
        double amount)
    {
        Drawing.Rectangle rect =
            new(
                0,
                0,
                bitmap.Width,
                bitmap.Height);

        BitmapData data =
            bitmap.LockBits(
                rect,
                ImageLockMode.ReadWrite,
                PixelFormat.Format32bppArgb);

        try
        {
            int stride =
                Math.Abs(data.Stride);

            int byteCount =
                stride *
                bitmap.Height;

            byte[] source =
                new byte[byteCount];

            byte[] target =
                new byte[byteCount];

            Marshal.Copy(
                data.Scan0,
                source,
                0,
                byteCount);

            Buffer.BlockCopy(
                source,
                0,
                target,
                0,
                byteCount);

            int RowOffset(int y) =>
                data.Stride >= 0
                    ? y * stride
                    : (bitmap.Height - 1 - y) *
                      stride;

            for (int y = 1;
                 y < bitmap.Height - 1;
                 y++)
            {
                int row =
                    RowOffset(y);

                int up =
                    RowOffset(y - 1);

                int down =
                    RowOffset(y + 1);

                for (int x = 1;
                     x < bitmap.Width - 1;
                     x++)
                {
                    int p =
                        row +
                        x * 4;

                    int alpha =
                        source[p + 3];

                    if (alpha <= 12)
                        continue;

                    int left =
                        p - 4;

                    int right =
                        p + 4;

                    int top =
                        up +
                        x * 4;

                    int bottom =
                        down +
                        x * 4;

                    for (int channel = 0;
                         channel < 3;
                         channel++)
                    {
                        int center =
                            source[p + channel];

                        int l =
                            source[left + 3] > 12
                                ? source[left + channel]
                                : center;

                        int r =
                            source[right + 3] > 12
                                ? source[right + channel]
                                : center;

                        int u =
                            source[top + 3] > 12
                                ? source[top + channel]
                                : center;

                        int d =
                            source[bottom + 3] > 12
                                ? source[bottom + channel]
                                : center;

                        double edge =
                            center * 4.0 -
                            l -
                            r -
                            u -
                            d;

                        int sharpened =
                            (int)Math.Round(
                                center +
                                amount * edge);

                        target[p + channel] =
                            (byte)Math.Clamp(
                                sharpened,
                                0,
                                255);
                    }
                }
            }

            Marshal.Copy(
                target,
                0,
                data.Scan0,
                byteCount);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
