using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using ThermalCamLib.Temperature;
using System.Threading.Tasks;
using System.Threading;


namespace ThermalCamLib
{
    public sealed class ThermalFrame
    {
        public ThermalFrame(int width, int height, ushort[] rawU16, byte[] gray8, ushort[] metaU16 = null, int metaRows = 4)
        {
            Width = width;
            Height = height;
            RawU16 = rawU16 ?? throw new ArgumentNullException(nameof(rawU16));
            Gray8 = gray8;
            MetaU16 = metaU16;
            MetaRows = Math.Max(0, metaRows);
            TimestampUtc = DateTime.UtcNow;
        }
        public int MetaRows { get; }
        public int ImageHeight => Math.Max(0, Height - MetaRows);


        private ThermalFrame _lastFrame;
        private object _frameLock = new object();

        public int Width { get; }
        public int Height { get; }
        public DateTime TimestampUtc { get; }

        /// <summary>
        /// 16-bit Rohwerte (typisch: Sensorwerte / „radiometric raw“).
        /// </summary>
        public ushort[] RawU16 { get; }

        /// <summary>
        /// Optionales 8-bit Graubild (z.B. aus RawU16 skaliert).
        /// </summary>
        public byte[] Gray8 { get; }

        /// <summary>
        /// Optional: Zusatzdaten (z.B. untere 4 Zeilen als u16).
        /// </summary>
        public ushort[] MetaU16 { get; }

        /// <summary>
        /// Auto-Contrast: nutzt vorhandenes Gray8 oder skaliert RawU16 per Min/Max auf 0..255.
        /// </summary>
        public Image ToGray8Image(int? metaRowsOverride = null)
        {
            int cropRows = Math.Max(0, metaRowsOverride ?? MetaRows);
            int outH = Math.Max(0, Height - cropRows);
            if (Width <= 0 || outH <= 0) throw new InvalidOperationException("Ungültige Bildgröße.");

            if (RawU16 == null || RawU16.Length != Width * Height)
                throw new InvalidOperationException("RawU16 fehlt oder hat falsche Länge.");

            int outCount = Width * outH;

            // Min/Max pro Frame neu bestimmen (nur sichtbarer Bereich)
            ushort min = ushort.MaxValue;
            ushort max = 0;
            for (int i = 0; i < outCount; i++)
            {
                ushort v = RawU16[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }

            int range = Math.Max(1, max - min);

            // Pro Frame neu skalieren
            var gray = new byte[outCount];
            for (int i = 0; i < outCount; i++)
                gray[i] = (byte)(((RawU16[i] - min) * 255) / range);

            return Create8bppGrayscaleBitmap(gray, Width, outH);
        }


        /// <summary>
        /// Temperaturbereich: minTempC -> 0, maxTempC -> 255 (clamped).
        /// Wenn converter null ist, wird eine Exception geworfen.
        /// </summary>
        public Image ToGray8Image(double minTempC, double maxTempC, ITemperatureConverter converter, int? metaRowsOverride = null)
        {
            if (converter == null) throw new ArgumentNullException(nameof(converter));
            if (maxTempC <= minTempC) throw new ArgumentException("maxTempC muss > minTempC sein.");

            int cropRows = Math.Max(0, metaRowsOverride ?? MetaRows);
            int outH = Math.Max(0, Height - cropRows);
            if (Width <= 0 || outH <= 0) throw new InvalidOperationException("Ungültige Bildgröße.");

            if (RawU16 == null || RawU16.Length != Width * Height)
                throw new InvalidOperationException("RawU16 fehlt oder hat falsche Länge.");

            int outCount = Width * outH;
            var gray = new byte[outCount];

            double range = maxTempC - minTempC;

            // Nur sichtbaren Teil (ohne die unteren Metadata-Zeilen) verarbeiten
            for (int i = 0; i < outCount; i++)
            {
                double t = converter.RawToCelsius(RawU16[i]);
                double f = (t - minTempC) / range;
                if (f <= 0) gray[i] = 0;
                else if (f >= 1) gray[i] = 255;
                else gray[i] = (byte)(f * 255.0);
            }

            return Create8bppGrayscaleBitmap(gray, Width, outH);
        }


        private byte[] GetGray8_AutoContrastCrop(int cropRows)
        {
            int outH = Math.Max(0, Height - cropRows);
            int outCount = Width * outH;

            if (Gray8 != null && Gray8.Length == Width * Height)
            {
                // Erst croppen, dann contrast (oder umgekehrt; so ist es stabiler)
                var cropped = new byte[outCount];
                Buffer.BlockCopy(Gray8, 0, cropped, 0, outCount);
                return AutoContrastByte(cropped);
            }

            if (RawU16 != null && RawU16.Length == Width * Height)
            {
                return ConvertRawToGray8_AutoContrast_Crop(RawU16, outCount);
            }

            throw new InvalidOperationException("Weder Gray8 noch RawU16 gültig.");
        }

        private static byte[] ConvertRawToGray8_AutoContrast_Crop(ushort[] raw, int outCount)
        {
            var gray = new byte[outCount];

            ushort min = ushort.MaxValue;
            ushort max = 0;

            for (int i = 0; i < outCount; i++)
            {
                ushort v = raw[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }

            int range = Math.Max(1, max - min);

            for (int i = 0; i < outCount; i++)
                gray[i] = (byte)(((raw[i] - min) * 255) / range);

            return gray;
        }



        public static class ColorMaps
           {
        public static Color[] Get(ColorMapType type)
        {
            switch (type)
            {
                case ColorMapType.Grayscale: return Grayscale();
                case ColorMapType.Iron: return Iron();
                case ColorMapType.Hot: return Hot();
                case ColorMapType.Jet: return Jet();
                case ColorMapType.Rainbow: return Rainbow();
                case ColorMapType.Turbo: return Turbo();
                case ColorMapType.Inferno: return Inferno();
                case ColorMapType.Viridis: return Viridis();
                default: return Grayscale();
            }
        }

        private static Color[] Grayscale()
        {
            var c = new Color[256];
            for (int i = 0; i < 256; i++)
                c[i] = Color.FromArgb(i, i, i);
            return c;
        }

        private static Color[] Hot()
        {
            var c = new Color[256];
            for (int i = 0; i < 256; i++)
            {
                int r = Math.Min(255, i * 3);
                int g = Math.Min(255, (i - 85) * 3);
                int b = Math.Min(255, (i - 170) * 3);
                c[i] = Color.FromArgb(
                    Clamp(r), Clamp(g), Clamp(b));
            }
            return c;
        }

        private static Color[] Iron()
        {
            var c = new Color[256];
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0;
                int r = (int)(255 * Math.Min(1, t * 1.5));
                int g = (int)(255 * Math.Max(0, t - 0.3));
                int b = (int)(255 * Math.Max(0, t - 0.6));
                c[i] = Color.FromArgb(Clamp(r), Clamp(g), Clamp(b));
            }
            return c;
        }

        private static Color[] Jet()
        {
            var c = new Color[256];
            for (int i = 0; i < 256; i++)
            {
                double x = i / 255.0;
                double r = Clamp01(1.5 - Math.Abs(4 * x - 3));
                double g = Clamp01(1.5 - Math.Abs(4 * x - 2));
                double b = Clamp01(1.5 - Math.Abs(4 * x - 1));
                c[i] = Color.FromArgb(
                    (int)(r * 255),
                    (int)(g * 255),
                    (int)(b * 255));
            }
            return c;
        }

        private static Color[] Rainbow()
        {
            var c = new Color[256];
            for (int i = 0; i < 256; i++)
                c[i] = HsvToRgb(i * 360.0 / 256.0, 1, 1);
            return c;
        }

        // ---- perceptually uniform (empfohlen!) ----

        private static Color[] Turbo() =>
            FromAnchorPoints(new[]
            {
            (0.00, Color.FromArgb(48,18,59)),
            (0.25, Color.FromArgb(50,131,189)),
            (0.50, Color.FromArgb(245,245,60)),
            (0.75, Color.FromArgb(252,141,89)),
            (1.00, Color.FromArgb(180,4,38))
            });

        private static Color[] Inferno() =>
            FromAnchorPoints(new[]
            {
            (0.00, Color.FromArgb(0,0,4)),
            (0.25, Color.FromArgb(87,15,109)),
            (0.50, Color.FromArgb(187,55,84)),
            (0.75, Color.FromArgb(249,142,8)),
            (1.00, Color.FromArgb(252,255,164))
            });

        private static Color[] Viridis() =>
            FromAnchorPoints(new[]
            {
            (0.00, Color.FromArgb(68,1,84)),
            (0.25, Color.FromArgb(59,82,139)),
            (0.50, Color.FromArgb(33,145,140)),
            (0.75, Color.FromArgb(94,201,98)),
            (1.00, Color.FromArgb(253,231,37))
            });

        // ---- helpers ----

        private static Color[] FromAnchorPoints((double t, Color c)[] p)
        {
            var map = new Color[256];
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0;
                int j = 0;
                while (j + 1 < p.Length && t > p[j + 1].t) j++;

                var a = p[j];
                var b = p[Math.Min(j + 1, p.Length - 1)];
                double u = (t - a.t) / Math.Max(1e-6, b.t - a.t);

                map[i] = Color.FromArgb(
                    Lerp(a.c.R, b.c.R, u),
                    Lerp(a.c.G, b.c.G, u),
                    Lerp(a.c.B, b.c.B, u));
            }
            return map;
        }

        private static Color HsvToRgb(double h, double s, double v)
        {
            h = (h % 360 + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = v - c;

            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }

            return Color.FromArgb(
                (int)((r + m) * 255),
                (int)((g + m) * 255),
                (int)((b + m) * 255));
        }

        private static int Lerp(int a, int b, double t) => (int)(a + (b - a) * t);
        private static int Clamp(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);
        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }


    private static byte[] AutoContrastByte(byte[] src)
        {
            int len = src.Length;
            byte min = 255, max = 0;
            for (int i = 0; i < len; i++)
            {
                byte v = src[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }

            int range = Math.Max(1, max - min);
            if (range == 255 && min == 0) return src;

            var dst = new byte[len];
            for (int i = 0; i < len; i++)
                dst[i] = (byte)(((src[i] - min) * 255) / range);

            return dst;
        }

        public enum ColorMapType
        {
            Grayscale,
            Iron,
            Hot,
            Jet,
            Rainbow,
            Turbo,      // sehr gut (Google Turbo)
            Inferno,
            Viridis
        }

        public Bitmap ToColorMappedImage(
    ColorMapType map,
    int? metaRowsOverride = null,
    bool autoContrast = true,
    ushort min = 0,
    ushort max = 65535)
        {
            int cropRows = Math.Max(0, metaRowsOverride ?? MetaRows);
            int outH = Math.Max(0, Height - cropRows);
            int w = Width;

            if (w <= 0 || outH <= 0) throw new InvalidOperationException("Ungültige Bildgröße.");
            if (RawU16 == null) throw new InvalidOperationException("RawU16 fehlt.");
            if (RawU16.Length < w * Height) throw new InvalidOperationException("RawU16 hat falsche Länge.");

            // Auto-Contrast Range bestimmen (nur sichtbarer Bereich)
            if (autoContrast)
            {
                ushort localMin = ushort.MaxValue;
                ushort localMax = 0;
                int count = w * outH;

                for (int i = 0; i < count; i++)
                {
                    ushort v = RawU16[i];
                    if (v < localMin) localMin = v;
                    if (v > localMax) localMax = v;
                }

                min = localMin;
                max = localMax;

                if (max == min)
                {
                    // kein Kontrast -> kleine Spreizung
                    max = (ushort)Math.Min(65535, min + 1);
                }
            }
            else
            {
                if (max <= min) max = (ushort)Math.Min(65535, min + 1);
            }

            var lut = ColorMaps.Get(map);
            var bmp = new Bitmap(w, outH, PixelFormat.Format24bppRgb);

            var rect = new Rectangle(0, 0, w, outH);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);

            try
            {
                int stride = data.Stride;
                int range = Math.Max(1, max - min);

                unsafe
                {
                    byte* dst = (byte*)data.Scan0;

                    for (int y = 0; y < outH; y++)
                    {
                        byte* row = dst + y * stride;
                        int src = y * w;

                        for (int x = 0; x < w; x++)
                        {
                            int v = RawU16[src + x];
                            int idx =
                                v <= min ? 0 :
                                v >= max ? 255 :
                                ((v - min) * 255) / range;

                            var c = lut[idx];
                            int p = x * 3;
                            row[p + 0] = c.B;
                            row[p + 1] = c.G;
                            row[p + 2] = c.R;
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }


        public Bitmap ToColorMappedImage(
    ColorMapType map,
    ushort min,
    ushort max,
    int? metaRowsOverride = null)
        {
            int cropRows = Math.Max(0, metaRowsOverride ?? MetaRows);
            int outH = Math.Max(0, Height - cropRows);
            int w = Width;

            if (RawU16 == null) throw new InvalidOperationException("RawU16 fehlt.");

            var lut = ColorMaps.Get(map);
            var bmp = new Bitmap(w, outH, PixelFormat.Format24bppRgb);

            var rect = new Rectangle(0, 0, w, outH);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);

            try
            {
                int stride = data.Stride;
                unsafe
                {
                    byte* dst = (byte*)data.Scan0;
                    int range = Math.Max(1, max - min);

                    for (int y = 0; y < outH; y++)
                    {
                        byte* row = dst + y * stride;
                        int src = y * w;

                        for (int x = 0; x < w; x++)
                        {
                            int v = RawU16[src + x];
                            int idx =
                                v <= min ? 0 :
                                v >= max ? 255 :
                                ((v - min) * 255) / range;

                            var c = lut[idx];
                            int p = x * 3;
                            row[p + 0] = c.B;
                            row[p + 1] = c.G;
                            row[p + 2] = c.R;
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }

        private static Bitmap Create8bppGrayscaleBitmap(byte[] gray, int w, int h)
        {
            if (gray == null) throw new ArgumentNullException(nameof(gray));
            if (gray.Length != w * h) throw new ArgumentException("gray hat falsche Länge.");

            var bmp = new Bitmap(w, h, PixelFormat.Format8bppIndexed);

            var pal = bmp.Palette;
            for (int i = 0; i < 256; i++)
                pal.Entries[i] = Color.FromArgb(i, i, i);
            bmp.Palette = pal;

            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);

            try
            {
                int stride = data.Stride;
                IntPtr dst = data.Scan0;

                for (int y = 0; y < h; y++)
                    Marshal.Copy(gray, y * w, dst + y * stride, w);
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }


    }
}

