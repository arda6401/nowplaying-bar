using System;
using System.Buffers;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NowPlayingBar.Services;

public static class ColorExtractor
{
    public static readonly Color DefaultAccent = Color.FromRgb(0x38, 0xA5, 0xFF); // #38A5FF

    public static Color ExtractDominantColor(BitmapSource? bitmapSource)
    {
        if (bitmapSource == null) return DefaultAccent;

        byte[]? rented = null;
        try
        {
            BitmapSource formattedSource = bitmapSource;
            if (bitmapSource.Format != PixelFormats.Bgra32)
            {
                formattedSource = new FormatConvertedBitmap(bitmapSource, PixelFormats.Bgra32, null, 0);
            }

            int width = formattedSource.PixelWidth;
            int height = formattedSource.PixelHeight;
            if (width <= 0 || height <= 0) return DefaultAccent;

            int stride = width * 4;
            int totalBytes = height * stride;

            // Use ArrayPool to eliminate GC allocation on every track change
            rented = ArrayPool<byte>.Shared.Rent(totalBytes);
            formattedSource.CopyPixels(rented, stride, 0);

            const int numBuckets = 18; // 20 deg per bucket
            Span<double> bucketWeights = stackalloc double[numBuckets];
            Span<double> bucketR = stackalloc double[numBuckets];
            Span<double> bucketG = stackalloc double[numBuckets];
            Span<double> bucketB = stackalloc double[numBuckets];

            int sampleStep = Math.Max(1, (width * height) / 800);

            double maxWeight = 0;
            int bestBucket = -1;
            int totalSampled = 0;
            double avgL = 0;

            for (int i = 0; i < totalBytes; i += 4 * sampleStep)
            {
                if (i + 3 >= totalBytes) break;

                byte b = rented[i];
                byte g = rented[i + 1];
                byte r = rented[i + 2];
                byte a = rented[i + 3];

                if (a < 128) continue; // skip transparent

                RgbToHsl(r, g, b, out double h, out double s, out double l);

                totalSampled++;
                avgL += l;

                // Skip extreme darks, lights, or un-saturated grays
                if (l < 0.15 || l > 0.92 || s < 0.18)
                    continue;

                double lWeight = 1.0 - Math.Abs(l - 0.52) * 1.8;
                if (lWeight < 0.1) lWeight = 0.1;
                double weight = s * s * lWeight;

                int bucket = (int)(h * (numBuckets / 360.0)) % numBuckets;
                bucketWeights[bucket] += weight;
                bucketR[bucket] += r * weight;
                bucketG[bucket] += g * weight;
                bucketB[bucket] += b * weight;

                if (bucketWeights[bucket] > maxWeight)
                {
                    maxWeight = bucketWeights[bucket];
                    bestBucket = bucket;
                }
            }

            if (bestBucket != -1 && maxWeight > 0.05)
            {
                double totalW = bucketWeights[bestBucket];
                byte r = (byte)Math.Clamp(bucketR[bestBucket] / totalW, 0, 255);
                byte g = (byte)Math.Clamp(bucketG[bestBucket] / totalW, 0, 255);
                byte b = (byte)Math.Clamp(bucketB[bestBucket] / totalW, 0, 255);

                RgbToHsl(r, g, b, out double h, out double s, out double l);

                // Boost luminance & saturation for dark taskbar background
                if (l < 0.52) l = 0.58;
                if (l > 0.82) l = 0.72;
                if (s < 0.55) s = Math.Min(1.0, s * 1.5);

                HslToRgb(h, s, l, out byte adjR, out byte adjG, out byte adjB);
                return Color.FromRgb(adjR, adjG, adjB);
            }

            if (totalSampled > 0)
            {
                avgL /= totalSampled;
                byte val = (byte)Math.Clamp(avgL > 0.5 ? 190 : 150, 0, 255);
                return Color.FromRgb(val, (byte)Math.Min(255, val + 15), (byte)Math.Min(255, val + 35));
            }

            return DefaultAccent;
        }
        catch
        {
            return DefaultAccent;
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static void RgbToHsl(byte r, byte g, byte b, out double h, out double s, out double l)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;

        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double diff = max - min;

        l = (max + min) / 2.0;

        if (Math.Abs(diff) < 0.00001)
        {
            h = 0;
            s = 0;
            return;
        }

        s = l > 0.5 ? diff / (2.0 - max - min) : diff / (max + min);

        if (Math.Abs(max - rd) < 0.00001)
        {
            h = (gd - bd) / diff + (gd < bd ? 6.0 : 0.0);
        }
        else if (Math.Abs(max - gd) < 0.00001)
        {
            h = (bd - rd) / diff + 2.0;
        }
        else
        {
            h = (rd - gd) / diff + 4.0;
        }

        h *= 60.0;
    }

    private static void HslToRgb(double h, double s, double l, out byte r, out byte g, out byte b)
    {
        if (Math.Abs(s) < 0.00001)
        {
            byte val = (byte)Math.Clamp(l * 255.0, 0, 255);
            r = val;
            g = val;
            b = val;
            return;
        }

        double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        double p = 2.0 * l - q;
        double hk = (h % 360.0) / 360.0;
        if (hk < 0) hk += 1.0;

        double tr = hk + 1.0 / 3.0;
        double tg = hk;
        double tb = hk - 1.0 / 3.0;

        r = (byte)Math.Clamp(ColorComponent(p, q, tr) * 255.0, 0, 255);
        g = (byte)Math.Clamp(ColorComponent(p, q, tg) * 255.0, 0, 255);
        b = (byte)Math.Clamp(ColorComponent(p, q, tb) * 255.0, 0, 255);
    }

    private static double ColorComponent(double p, double q, double t)
    {
        if (t < 0) t += 1.0;
        if (t > 1) t -= 1.0;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }
}
