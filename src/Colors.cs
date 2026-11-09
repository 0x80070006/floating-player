using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatingPlayer
{
    // The full set of colors the bar is drawn with.
    class ColorScheme
    {
        public Color Background, Foreground, Secondary, Accent, AccentForeground, Hover, Pressed;
        public bool Dark;

        public static readonly Color NeutralBase = Color.FromRgb(0x24, 0x25, 0x28);

        public static ColorScheme Neutral()
        {
            return Build(NeutralBase, Color.FromRgb(0xC8, 0xCA, 0xD0));
        }

        public static ColorScheme FromCustom(Color color)
        {
            double h, s, l;
            ColorMath.ToHsl(color, out h, out s, out l);
            return BuildExact(color, ColorMath.FromHsl(h, Math.Min(s, 0.8), l > 0.5 ? 0.3 : 0.75));
        }

        // Keeps the character of the artwork but limits it to tones text stays readable on.
        public static ColorScheme Build(Color dominant, Color vivid)
        {
            double h, s, l;
            ColorMath.ToHsl(dominant, out h, out s, out l);
            bool light = l > 0.72;
            Color background = light
                ? ColorMath.FromHsl(h, Math.Min(s, 0.45), Clamp(l, 0.8, 0.9))
                : ColorMath.FromHsl(h, Math.Min(s, 0.6), Clamp(l, 0.13, 0.3));
            return BuildExact(background, vivid);
        }

        static ColorScheme BuildExact(Color background, Color accent)
        {
            bool dark = ColorMath.Contrast(background, Colors.White) >= ColorMath.Contrast(background, Colors.Black);
            Color foreground = ColorMath.Mix(dark ? Color.FromRgb(0xF7, 0xF7, 0xF7) : Color.FromRgb(0x14, 0x14, 0x14), background, 0.06);
            accent = ColorMath.EnsureContrast(accent, background, 3.0);
            return new ColorScheme
            {
                Dark = dark,
                Background = background,
                Foreground = foreground,
                Secondary = ColorMath.Mix(foreground, background, 0.32),
                Accent = accent,
                AccentForeground = ColorMath.Contrast(accent, Colors.White) >= ColorMath.Contrast(accent, Colors.Black)
                    ? Colors.White : Color.FromRgb(0x10, 0x10, 0x10),
                Hover = Color.FromArgb(0x24, foreground.R, foreground.G, foreground.B),
                Pressed = Color.FromArgb(0x14, foreground.R, foreground.G, foreground.B)
            };
        }

        static double Clamp(double v, double min, double max) { return Math.Max(min, Math.Min(max, v)); }
    }

    static class ColorMath
    {
        public static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            l = (max + min) / 2;
            s = d < 1e-6 ? 0 : d / (1 - Math.Abs(2 * l - 1));
            if (d < 1e-6) h = 0;
            else if (max == r) h = 60 * (((g - b) / d + 6) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }

        public static Color FromHsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
        }

        static byte ToByte(double v) { return (byte)Math.Max(0, Math.Min(255, Math.Round(v * 255))); }

        public static Color Mix(Color a, Color b, double amountOfB)
        {
            Func<byte, byte, byte> lerp = (x, y) => (byte)Math.Round(x + (y - x) * amountOfB);
            return Color.FromRgb(lerp(a.R, b.R), lerp(a.G, b.G), lerp(a.B, b.B));
        }

        static double Luminance(Color c)
        {
            Func<byte, double> channel = v =>
            {
                double x = v / 255.0;
                return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            };
            return 0.2126 * channel(c.R) + 0.7152 * channel(c.G) + 0.0722 * channel(c.B);
        }

        public static double Contrast(Color a, Color b)
        {
            double x = Luminance(a), y = Luminance(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        // Moves the lightness away from the background until the target contrast is reached.
        public static Color EnsureContrast(Color color, Color background, double target)
        {
            if (Contrast(color, background) >= target) return color;
            double h, s, l;
            ToHsl(color, out h, out s, out l);
            int direction = Luminance(background) < 0.25 ? 1 : -1;
            for (int i = 0; i < 40; i++)
            {
                l = Math.Max(0, Math.Min(1, l + direction * 0.025));
                Color candidate = FromHsl(h, s, l);
                if (Contrast(candidate, background) >= target) return candidate;
            }
            return direction > 0 ? Colors.White : Colors.Black;
        }

        public static string ToHex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        public static bool TryParse(string text, out Color color)
        {
            color = Colors.Black;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string hex = text.Trim().TrimStart('#');
            int value;
            if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out value)) return false;
            color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }
    }

    // Extracts the colors of a cover image. Safe to call from any thread.
    static class CoverAnalyzer
    {
        class Bin { public long R, G, B; public int Count; }

        public static BitmapImage Decode(byte[] data)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(data);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 192;
            image.EndInit();
            image.Freeze();
            return image;
        }

        public static ColorScheme Analyze(BitmapSource image)
        {
            const int Size = 48;
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var scaled = new TransformedBitmap(converted, new ScaleTransform((double)Size / converted.PixelWidth, (double)Size / converted.PixelHeight));
            int width = scaled.PixelWidth, height = scaled.PixelHeight;
            var pixels = new byte[width * height * 4];
            scaled.CopyPixels(pixels, width * 4, 0);

            var bins = new Dictionary<int, Bin>();
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 128) continue;
                int key = (pixels[i + 2] >> 4) << 8 | (pixels[i + 1] >> 4) << 4 | pixels[i] >> 4;
                Bin bin;
                if (!bins.TryGetValue(key, out bin)) bins[key] = bin = new Bin();
                bin.R += pixels[i + 2]; bin.G += pixels[i + 1]; bin.B += pixels[i]; bin.Count++;
            }
            if (bins.Count == 0) return ColorScheme.Neutral();

            var colors = bins.Values.Select(b => new
            {
                Color = Color.FromRgb((byte)(b.R / b.Count), (byte)(b.G / b.Count), (byte)(b.B / b.Count)),
                b.Count
            }).Select(b =>
            {
                double h, s, l;
                ColorMath.ToHsl(b.Color, out h, out s, out l);
                return new { b.Color, b.Count, H = h, S = s, L = l };
            }).ToList();

            // Letterboxing and pure white backgrounds say nothing about the picture.
            var usable = colors.Where(c => c.L > 0.05 && c.L < 0.96).ToList();
            if (usable.Count == 0) return ColorScheme.Neutral();

            var dominant = usable.OrderByDescending(c => c.Count * (0.35 + c.S)).First();
            var vivid = usable.OrderByDescending(c =>
            {
                double middle = 1 - Math.Abs(c.L - 0.55) * 1.4;
                return Math.Sqrt(c.Count) * c.S * c.S * Math.Max(0.05, middle);
            }).First();

            Color accent = vivid.S >= 0.22
                ? vivid.Color
                : ColorMath.FromHsl(dominant.H, Math.Min(dominant.S, 0.25), 0.78);
            return ColorScheme.Build(dominant.Color, accent);
        }
    }
}
