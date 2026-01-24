using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ThermalCamLib.LockIn;

public static class LockInPhaseImaging
{
    public static Bitmap CreatePhaseHueBitmap(
        LockInAccumulator acc,
        bool normalize = true,
        double? ampMin = null,
        double? ampMax = null,
        double saturation = 1.0)
    {
        int w = acc.Width;
        int h = acc.VisibleHeight;
        int nPix = w * h;

        var amp = acc.GetAmplitude(normalize);
        var phase = acc.GetPhase();

        double aMin = ampMin ?? Min(amp);
        double aMax = ampMax ?? Max(amp);
        double aRange = Math.Max(1e-12, aMax - aMin);

        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);

        try
        {
            int stride = data.Stride;
            byte[] bytes = new byte[stride * h];

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int baseIdx = y * w;

                for (int x = 0; x < w; x++)
                {
                    int i = baseIdx + x;

                    // Hue aus Phase
                    double hue = (phase[i] + Math.PI) * (180.0 / Math.PI); // 0..360

                    // Value aus Amplitude
                    double v = (amp[i] - aMin) / aRange;
                    if (v < 0) v = 0;
                    if (v > 1) v = 1;

                    var c = HsvToColor(hue, saturation, v);

                    int p = row + x * 3;
                    bytes[p + 0] = c.B;
                    bytes[p + 1] = c.G;
                    bytes[p + 2] = c.R;
                }
            }

            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bmp;
    }

    private static double Min(double[] a) { double m = double.PositiveInfinity; for (int i = 0; i < a.Length; i++) if (a[i] < m) m = a[i]; return m; }
    private static double Max(double[] a) { double m = double.NegativeInfinity; for (int i = 0; i < a.Length; i++) if (a[i] > m) m = a[i]; return m; }

    private static Color HsvToColor(double hDeg, double s, double v)
    {
        hDeg = (hDeg % 360 + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((hDeg / 60.0) % 2 - 1));
        double m = v - c;

        double r1, g1, b1;
        if (hDeg < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (hDeg < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (hDeg < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (hDeg < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (hDeg < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        byte r = (byte)Math.Round((r1 + m) * 255);
        byte g = (byte)Math.Round((g1 + m) * 255);
        byte b = (byte)Math.Round((b1 + m) * 255);

        return Color.FromArgb(r, g, b);
    }
}
