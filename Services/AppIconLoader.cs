using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using Avalonia.Media.Imaging;
using AvColor = Avalonia.Media.Color;
using Bitmap = Avalonia.Media.Imaging.Bitmap;
using SdBitmap = System.Drawing.Bitmap;
using SdColor = System.Drawing.Color;

namespace Daylane.Services;

internal static class AppIconLoader
{
    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Get(string? exePath) => Resolve(exePath).Bitmap;

    public static AvColor GetAccent(string? exePath, string? processName = null)
    {
        Entry entry = Resolve(exePath);
        if (entry.HasAccent)
        {
            return entry.Accent;
        }

        return FromHash(string.IsNullOrEmpty(exePath) ? processName ?? "" : exePath!);
    }

    private static readonly AvColor[] FallbackPalette =
    [
        AvColor.Parse("#3B82F6"),
        AvColor.Parse("#2F9E6B"),
        AvColor.Parse("#F59E0B"),
        AvColor.Parse("#EF4444"),
        AvColor.Parse("#8B5CF6"),
        AvColor.Parse("#06B6D4"),
        AvColor.Parse("#EC4899"),
        AvColor.Parse("#84CC16"),
        AvColor.Parse("#F97316"),
        AvColor.Parse("#6366F1")
    ];

    private static AvColor FromHash(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return AvColor.Parse("#6B7280");
        }

        int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(key);
        int index = Math.Abs(hash) % FallbackPalette.Length;
        return FallbackPalette[index];
    }

    private static Entry Resolve(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return Entry.Empty;
        }

        return Cache.GetOrAdd(exePath, Load);
    }

    private static Entry Load(string path)
    {
        if (!File.Exists(path))
        {
            return Entry.Empty;
        }

        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return Entry.Empty;
            }

            using SdBitmap frame = icon.ToBitmap();
            AvColor accent = ExtractMainColor(frame);
            using var ms = new MemoryStream();
            frame.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            return new Entry(new Bitmap(ms), accent, true);
        }
        catch
        {
            return Entry.Empty;
        }
    }

    // Chromatic icons: weighted hue peak. Mono/dark icons: dominant luminance (skip near-white).
    private static AvColor ExtractMainColor(SdBitmap bmp)
    {
        const int HueBins = 360;
        const int LumBins = 32;
        const int SmoothRadius = 18;
        const int AverageRadius = 22;

        var weight = new double[HueBins];
        var sumR = new double[HueBins];
        var sumG = new double[HueBins];
        var sumB = new double[HueBins];

        var lumCount = new int[LumBins];
        var lumR = new long[LumBins];
        var lumG = new long[LumBins];
        var lumB = new long[LumBins];

        int opaque = 0;
        int chromatic = 0;
        double chromaWeight = 0;

        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                SdColor c = bmp.GetPixel(x, y);
                if (c.A < 140)
                {
                    continue;
                }

                // Highlights / white marks are never the plate color.
                if (c.R > 245 && c.G > 245 && c.B > 245)
                {
                    continue;
                }

                opaque++;
                int lum = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                int lumKey = Math.Min(LumBins - 1, lum * LumBins / 256);
                lumCount[lumKey]++;
                lumR[lumKey] += c.R;
                lumG[lumKey] += c.G;
                lumB[lumKey] += c.B;

                if (c.R < 40 && c.G < 40 && c.B < 40)
                {
                    continue;
                }

                int max = Math.Max(c.R, Math.Max(c.G, c.B));
                int min = Math.Min(c.R, Math.Min(c.G, c.B));
                int chroma = max - min;
                if (chroma < 20)
                {
                    continue;
                }

                float sat = chroma / (float)max;
                if (sat < 0.22f)
                {
                    continue;
                }

                float light = (max + min) / (2f * 255f);
                float lightBias = 1f - Math.Abs(light - 0.48f) * 2.2f;
                if (lightBias < 0.08f)
                {
                    continue;
                }

                float w = sat * sat * lightBias;
                int hue = HueDegrees(c.R, c.G, c.B, max, chroma);
                weight[hue] += w;
                sumR[hue] += c.R * w;
                sumG[hue] += c.G * w;
                sumB[hue] += c.B * w;
                chromatic++;
                chromaWeight += w;
            }
        }

        if (opaque == 0)
        {
            return AvColor.Parse("#6B7280");
        }

        // Cursor-like mono marks: almost no real chroma, dark plate is the brand.
        if (chromatic == 0 || chromatic < opaque * 0.15 || chromaWeight < opaque * 0.03)
        {
            return FromLuminance(lumCount, lumR, lumG, lumB);
        }

        double[] smooth = SmoothCircular(weight, SmoothRadius);
        int peak = 0;
        double peakVal = smooth[0];
        for (int i = 1; i < HueBins; i++)
        {
            if (smooth[i] > peakVal)
            {
                peakVal = smooth[i];
                peak = i;
            }
        }

        double totalW = 0;
        double r = 0, g = 0, b = 0;
        for (int d = -AverageRadius; d <= AverageRadius; d++)
        {
            int h = (peak + d + HueBins) % HueBins;
            if (weight[h] <= 0)
            {
                continue;
            }

            double falloff = 1.0 - Math.Abs(d) / (double)(AverageRadius + 1);
            totalW += weight[h] * falloff;
            r += sumR[h] * falloff;
            g += sumG[h] * falloff;
            b += sumB[h] * falloff;
        }

        if (totalW <= 0)
        {
            return FromLuminance(lumCount, lumR, lumG, lumB);
        }

        return AvColor.FromRgb(
            (byte)Math.Clamp((int)Math.Round(r / totalW), 0, 255),
            (byte)Math.Clamp((int)Math.Round(g / totalW), 0, 255),
            (byte)Math.Clamp((int)Math.Round(b / totalW), 0, 255));
    }

    private static AvColor FromLuminance(int[] count, long[] sumR, long[] sumG, long[] sumB)
    {
        int best = 0;
        int bestCount = count[0];
        for (int i = 1; i < count.Length; i++)
        {
            if (count[i] > bestCount)
            {
                bestCount = count[i];
                best = i;
            }
        }

        if (bestCount <= 0)
        {
            return AvColor.Parse("#6B7280");
        }

        return AvColor.FromRgb(
            (byte)(sumR[best] / bestCount),
            (byte)(sumG[best] / bestCount),
            (byte)(sumB[best] / bestCount));
    }

    private static int HueDegrees(int r, int g, int b, int max, int chroma)
    {
        float sector;
        if (max == r)
        {
            sector = (g - b) / (float)chroma;
        }
        else if (max == g)
        {
            sector = ((b - r) / (float)chroma) + 2f;
        }
        else
        {
            sector = ((r - g) / (float)chroma) + 4f;
        }

        if (sector < 0)
        {
            sector += 6f;
        }

        int hue = (int)(sector * 60f);
        if (hue >= 360)
        {
            hue = 0;
        }

        return hue;
    }

    private static double[] SmoothCircular(double[] src, int radius)
    {
        int n = src.Length;
        var dst = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int d = -radius; d <= radius; d++)
            {
                double k = 1.0 - Math.Abs(d) / (double)(radius + 1);
                sum += src[(i + d + n) % n] * k;
            }

            dst[i] = sum;
        }

        return dst;
    }

    private readonly record struct Entry(Bitmap? Bitmap, AvColor Accent, bool HasAccent)
    {
        public static Entry Empty { get; } = new(null, default, false);
    }
}
