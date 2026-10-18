using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpotifyNotch;

/// <summary>Picks a bright, saturated accent colour from album art.</summary>
internal static class ArtColor
{
    public static readonly Color Fallback = Color.FromRgb(0x1E, 0xD7, 0x60);

    public static Color FromBytes(byte[] data)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 32;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();

            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            const int buckets = 12;
            var weight = new double[buckets];
            var sr = new double[buckets];
            var sg = new double[buckets];
            var sb = new double[buckets];

            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                ToHsv(r, g, b, out double hue, out double s, out double v);
                double wgt = s * v * v + 0.01;
                int k = (int)(hue / 30.0) % buckets;
                weight[k] += wgt;
                sr[k] += r * wgt;
                sg[k] += g * wgt;
                sb[k] += b * wgt;
            }

            int best = 0;
            for (int k = 1; k < buckets; k++)
                if (weight[k] > weight[best]) best = k;

            double ar = sr[best] / weight[best], ag = sg[best] / weight[best], ab = sb[best] / weight[best];
            ToHsv(ar, ag, ab, out double bh, out double bs, out double bv);
            if (bs < 0.12) return Color.FromRgb(0xE6, 0xE6, 0xE6); // greyscale art

            bs = Math.Clamp(bs, 0.45, 0.95);
            bv = Math.Clamp(bv, 0.80, 1.0);
            FromHsv(bh, bs, bv, out byte rr, out byte gg, out byte bb);
            return Color.FromRgb(rr, gg, bb);
        }
        catch
        {
            return Fallback;
        }
    }

    /// <summary>
    /// Three vivid, clearly different colours from the cover, most dominant first. Greyscale covers give soft greys.
    /// </summary>
    public static Color[] PaletteFromBytes(byte[] data)
    {
        Color[] greys = { Color.FromRgb(0xE6, 0xE6, 0xE6), Color.FromRgb(0xB4, 0xB4, 0xB4), Color.FromRgb(0x8C, 0x8C, 0x8C) };
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 32;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();

            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            const int buckets = 12;
            var weight = new double[buckets];
            var sr = new double[buckets];
            var sg = new double[buckets];
            var sb = new double[buckets];
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                ToHsv(r, g, b, out double hue, out double s, out double v);
                double wgt = s * v * v + 0.01;
                int k = (int)(hue / 30.0) % buckets;
                weight[k] += wgt;
                sr[k] += r * wgt;
                sg[k] += g * wgt;
                sb[k] += b * wgt;
            }

            var order = new List<int>();
            for (int k = 0; k < buckets; k++) order.Add(k);
            order.Sort((a, c) => weight[c].CompareTo(weight[a]));

            var chosen = new List<(double h, double s, double v)>();
            foreach (int k in order)
            {
                if (chosen.Count == 3) break;
                if (weight[k] <= 0) continue;
                if (chosen.Count > 0 && weight[k] < weight[order[0]] * 0.12) break; // too little of this colour to matter
                ToHsv(sr[k] / weight[k], sg[k] / weight[k], sb[k] / weight[k], out double bh, out double bs, out double bv);
                if (bs < 0.12) continue; // greyish
                bool distinct = true;
                foreach (var c in chosen)
                {
                    double d = Math.Abs(c.h - bh);
                    if (Math.Min(d, 360 - d) < 35) { distinct = false; break; }
                }
                if (distinct) chosen.Add((bh, bs, bv));
            }

            if (chosen.Count == 0) return greys;

            // monochrome-ish covers: invent neighbours by rotating the hue and varying the brightness
            for (int n = 1; chosen.Count < 3; n++)
            {
                var first = chosen[0];
                double shift = (n % 2 == 1 ? 1 : -1) * 32 * ((n + 1) / 2);
                chosen.Add(((first.h + shift + 360) % 360, first.s, first.v * (n % 2 == 1 ? 1.0 : 0.9)));
            }

            var colors = new Color[3];
            for (int i = 0; i < 3; i++)
            {
                FromHsv(chosen[i].h, Math.Clamp(chosen[i].s, 0.5, 0.95), Math.Clamp(chosen[i].v, 0.85, 1.0), out byte r, out byte g, out byte b);
                colors[i] = Color.FromRgb(r, g, b);
            }
            // blob order left / centre / right: the dominant colour goes in the big centre blob
            return new[] { colors[1], colors[0], colors[2] };
        }
        catch
        {
            return greys;
        }
    }

    /// <summary>
    /// Rotates a vivid neon colour part of the way toward the album's hue so every track gets its own mood
    /// while the result stays neon. Greyscale art leaves the neon unchanged.
    /// </summary>
    public static Color TintToward(Color neon, Color album, double amount)
    {
        ToHsv(neon.R / 255.0, neon.G / 255.0, neon.B / 255.0, out double nh, out double ns, out double nv);
        ToHsv(album.R / 255.0, album.G / 255.0, album.B / 255.0, out double ah, out double asat, out _);
        if (asat < 0.15) return neon;

        double delta = ((ah - nh + 540) % 360) - 180; // shortest way round the colour wheel
        double h = (nh + delta * amount + 360) % 360;
        FromHsv(h, Math.Max(ns, 0.7), Math.Max(nv, 0.95), out byte r, out byte g, out byte b);
        return Color.FromRgb(r, g, b);
    }

    static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * (((b - r) / d) + 2);
        else h = 60 * (((r - g) / d) + 4);
        if (h < 0) h += 360;
    }

    static void FromHsv(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        double rr, gg, bb;
        if (h < 60) (rr, gg, bb) = (c, x, 0);
        else if (h < 120) (rr, gg, bb) = (x, c, 0);
        else if (h < 180) (rr, gg, bb) = (0, c, x);
        else if (h < 240) (rr, gg, bb) = (0, x, c);
        else if (h < 300) (rr, gg, bb) = (x, 0, c);
        else (rr, gg, bb) = (c, 0, x);
        r = (byte)Math.Round((rr + m) * 255);
        g = (byte)Math.Round((gg + m) * 255);
        b = (byte)Math.Round((bb + m) * 255);
    }
}
