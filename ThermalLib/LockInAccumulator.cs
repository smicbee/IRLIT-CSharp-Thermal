using System;
using System.Collections.Generic;
using System.Drawing.Imaging;

namespace ThermalCamLib.LockIn
{
    public sealed class LockInAccumulator
    {
        private readonly int _w, _h, _metaRows;
        private readonly double _fHz;
        private readonly double _w0; // 2π f

        private readonly double[] _I;
        private readonly double[] _Q;

        private int _n;
        private double _t0;
        private bool _started;

        // Phase-bin ON/OFF Summen
        private readonly int _bins;
        private readonly double[][] _sumOn;
        private readonly double[][] _sumOff;
        private readonly int[] _cntOn;
        private readonly int[] _cntOff;

        // Optional DC
        private double[] _sumX;

        public LockInAccumulator(
            int width,
            int height,
            double frequencyHz,
            int metaRows = 0,
            int phaseBins = 36,     // z.B. 36 = 10°-Bins, 72 = 5°
            bool trackDc = true)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (frequencyHz <= 0) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
            if (phaseBins <= 0) throw new ArgumentOutOfRangeException(nameof(phaseBins));

            _w = width;
            _h = height;
            _metaRows = Math.Max(0, metaRows);

            _fHz = frequencyHz;
            _w0 = 2.0 * Math.PI * _fHz;

            _bins = phaseBins;

            int outH = Math.Max(0, _h - _metaRows);
            int nPix = _w * outH;

            _I = new double[nPix];
            _Q = new double[nPix];

            if (trackDc)
                _sumX = new double[nPix];

            _sumOn = new double[_bins][];
            _sumOff = new double[_bins][];
            _cntOn = new int[_bins];
            _cntOff = new int[_bins];

            for (int b = 0; b < _bins; b++)
            {
                _sumOn[b] = new double[nPix];
                _sumOff[b] = new double[nPix];
            }
        }

        public int Width => _w;
        public int Height => _h;
        public int MetaRows => _metaRows;
        public int VisibleHeight => Math.Max(0, _h - _metaRows);
        public int PixelCount => _w * VisibleHeight;
        public int SampleCount => _n;
        public double FrequencyHz => _fHz;
        public int PhaseBins => _bins;

        public void Reset(double? t0Seconds = null)
        {
            Array.Clear(_I, 0, _I.Length);
            Array.Clear(_Q, 0, _Q.Length);
            if (_sumX != null) Array.Clear(_sumX, 0, _sumX.Length);

            for (int b = 0; b < _bins; b++)
            {
                Array.Clear(_sumOn[b], 0, _sumOn[b].Length);
                Array.Clear(_sumOff[b], 0, _sumOff[b].Length);
                _cntOn[b] = 0;
                _cntOff[b] = 0;
            }

            _n = 0;
            _started = false;
            _t0 = t0Seconds ?? 0.0;
            if (t0Seconds.HasValue) _started = true;
        }

        private int PhaseToBin(double phaseRad0To2Pi)
        {
            // phase in [0..2π)
            int bin = (int)(phaseRad0To2Pi / (2.0 * Math.PI) * _bins);
            if (bin < 0) bin = 0;
            if (bin >= _bins) bin = _bins - 1;
            return bin;
        }

        /// <summary>
        /// AddFrame mit Stimulus-Status: sammelt ON/OFF Summen pro Phase-Bin.
        /// </summary>
        public void AddFrame(
            ThermalFrame frame,
            double timeSeconds,
            bool stimulusOn,
            Func<ushort, double> xSelector = null)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.RawU16 == null) throw new InvalidOperationException("Frame.RawU16 fehlt.");
            if (frame.Width != _w || frame.Height != _h)
                throw new InvalidOperationException("Frame-Größe passt nicht zum Accumulator.");
            if (frame.RawU16.Length != _w * _h)
                throw new InvalidOperationException("Frame.RawU16 hat falsche Länge.");

            if (!_started)
            {
                _t0 = timeSeconds;
                _started = true;
            }

            double t = timeSeconds - _t0;

            // Phase in [0..2π)
            double phi = (_w0 * t) % (2.0 * Math.PI);
            if (phi < 0) phi += 2.0 * Math.PI;

            int bin = PhaseToBin(phi);

            // Optional weiterhin I/Q führen (falls du zusätzlich klassische Lock-in Auswertung willst)
            double c = Math.Cos(_w0 * t);
            double s = Math.Sin(_w0 * t);

            int nPix = PixelCount;
            if (xSelector == null) xSelector = raw => raw;

            double[] target = stimulusOn ? _sumOn[bin] : _sumOff[bin];

            for (int i = 0; i < nPix; i++)
            {
                double x = xSelector(frame.RawU16[i]);

                _I[i] += x * c;
                _Q[i] += x * s;
                if (_sumX != null) _sumX[i] += x;

                target[i] += x;
            }

            if (stimulusOn) _cntOn[bin]++; else _cntOff[bin]++;
            _n++;
        }


        public Dictionary<double, ThermalFrame> GetAllAngleFrames(
    bool shiftedOnOff = true,
    bool useMean = true,
    int exportMaxAngleDeg = 180,              // 180 reicht (siehe vorher)
    ThermalCamLib.ThermalFrame.ColorMapType? colormap = null,
    bool autoContrast = true,
    ushort fixedMin = 0,
    ushort fixedMax = 65535)
        {

            // Sinnvoll: pro Bin genau ein Winkel, sonst sind viele Winkel gleich
            // Winkelmitte je Bin (0..180)
            int binsToExport = Math.Min(_bins, (int)Math.Round(_bins * (exportMaxAngleDeg / 360.0)) * 2);
            // einfacher: exportiere alle Bins und begrenze später per Winkel

            Dictionary<double, ThermalFrame> outDict = new Dictionary<double, ThermalFrame>();

            for (int b = 0; b < _bins; b++)
            {
                // Bin-Mitte in Grad
                double angle = (b + 0.5) * 360.0 / _bins;

                if (angle >= exportMaxAngleDeg + 1e-9) continue; // 0..180

                ThermalFrame frame = shiftedOnOff
                    ? GetFrameAtAngle(angle, useMean: useMean)
                    : GetFrameAtAngle(angle, useMean: useMean);

                outDict.Add(angle, frame);

            }

            return outDict;
        }

            /// <summary>
            /// Für Winkel (Grad): nimmt den passenden Phase-Bin und liefert (MeanOn - MeanOff) als Frame.
            /// angleDeg bezieht sich auf die Zeitphase 0..360° (phi = 2π f t).
            /// </summary>
            public ThermalFrame GetFrameAtAngle(
           double angleDeg,
           bool useMean = true,
           double? signalMin = null,
           double? signalMax = null)
        {
            if (_n <= 0) throw new InvalidOperationException("Keine Samples im LockInAccumulator.");

            // angle in [0..360)
            double aOn = angleDeg % 360.0;
            if (aOn < 0) aOn += 360.0;

            // OFF ist 180° versetzt
            double aOff = (aOn + 180.0) % 360.0;

            int binOn = (int)(aOn / 360.0 * _bins);
            int binOff = (int)(aOff / 360.0 * _bins);

            if (binOn < 0) binOn = 0;
            if (binOn >= _bins) binOn = _bins - 1;
            if (binOff < 0) binOff = 0;
            if (binOff >= _bins) binOff = _bins - 1;

            int nPix = PixelCount;

            int nOn = _cntOn[binOn];
            int nOff = _cntOff[binOff];

            if (nOn == 0 || nOff == 0)
                throw new InvalidOperationException(
                    $"Zu wenige Samples: ON@{aOn:F1}° bin {binOn} (ON={nOn}), OFF@{aOff:F1}° bin {binOff} (OFF={nOff}).");

            var diff = new double[nPix];

            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;

            for (int i = 0; i < nPix; i++)
            {
                double on = _sumOn[binOn][i];
                double off = _sumOff[binOff][i];

                if (useMean)
                {
                    on /= nOn;
                    off /= nOff;
                }

                double v = on - off;
                diff[i] = v;
                if (v < min) min = v;
                if (v > max) max = v;
            }

            double lo = signalMin ?? min;
            double hi = signalMax ?? max;
            double range = hi - lo;

            if (range <= 1e-12)
            {
                var flat = new ushort[nPix];
                for (int i = 0; i < nPix; i++) flat[i] = 32768;
                return new ThermalFrame(_w, VisibleHeight, flat, gray8: null, metaU16: null, metaRows: 0);
            }

            var raw = new ushort[nPix];
            for (int i = 0; i < nPix; i++)
            {
                double f = (diff[i] - lo) / range;
                if (f <= 0) raw[i] = 0;
                else if (f >= 1) raw[i] = 65535;
                else raw[i] = (ushort)(f * 65535.0);
            }

            return new ThermalFrame(_w, VisibleHeight, raw, gray8: null, metaU16: null, metaRows: 0);
        }



        public double[] GetAmplitude(bool normalize = true, bool removeDc = false)
        {
            int nPix = PixelCount;
            var amp = new double[nPix];

            double norm = normalize && _n > 0 ? (2.0 / _n) : 1.0;

            for (int i = 0; i < nPix; i++)
            {
                double I = _I[i];
                double Q = _Q[i];

                // DC removal in I/Q ist nicht direkt nötig; DC ist orthogonal zu sin/cos über ganze Perioden.
                // Wenn du aber nicht integer Perioden integrierst, kann DC "leaken".
                if (removeDc && _sumX != null && _n > 0)
                {
                    // einfache Leak-Korrektur: nichts am I/Q ändern, aber optional hier Platz für eine Windowing-Strategie.
                    // (Bei Bedarf kann ich dir eine saubere Hann-Window + DC-Block Variante geben.)
                }

                amp[i] = norm * Math.Sqrt(I * I + Q * Q);
            }

            return amp;
        }

        /// <summary>
        /// Phase pro Pixel in Radiant (-π..π), Länge = PixelCount.
        /// </summary>
        public double[] GetPhase()
        {
            int nPix = PixelCount;
            var ph = new double[nPix];
            for (int i = 0; i < nPix; i++)
                ph[i] = Math.Atan2(_Q[i], _I[i]);
            return ph;
        }

        /// <summary>
        /// Roh I/Q herausgeben (z.B. für eigene Normalisierung).
        /// </summary>
        public (double[] I, double[] Q) GetIQ()
        {
            var I = (double[])_I.Clone();
            var Q = (double[])_Q.Clone();
            return (I, Q);
        }


        public ThermalFrame GetAmplitudeFrame(
    bool normalize = true,
    double? ampMin = null,
    double? ampMax = null)
        {
            if (_n <= 0) throw new InvalidOperationException("Keine Samples im LockInAccumulator.");

            int w = _w;
            int h = VisibleHeight;
            int nPix = PixelCount;

            double norm = normalize ? (2.0 / _n) : 1.0;

            // Amplitude berechnen + Min/Max finden
            var amp = new double[nPix];
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;

            for (int i = 0; i < nPix; i++)
            {
                double I = norm * _I[i];
                double Q = norm * _Q[i];
                double a = Math.Sqrt(I * I + Q * Q);
                amp[i] = a;
                if (a < min) min = a;
                if (a > max) max = a;
            }

            double lo = ampMin ?? min;
            double hi = ampMax ?? max;
            double range = hi - lo;

            if (range <= 1e-12)
            {
                var flat = new ushort[nPix];
                for (int i = 0; i < nPix; i++) flat[i] = 0;
                return new ThermalFrame(w, h, flat, gray8: null, metaU16: null, metaRows: 0);
            }

            var raw = new ushort[nPix];
            for (int i = 0; i < nPix; i++)
            {
                double f = (amp[i] - lo) / range;   // 0..1
                if (f <= 0) raw[i] = 0;
                else if (f >= 1) raw[i] = 65535;
                else raw[i] = (ushort)(f * 65535.0);
            }

            return new ThermalFrame(w, h, raw, gray8: null, metaU16: null, metaRows: 0);
        }

        public ThermalFrame GetPhaseFrame(
    bool normalize = true,
    bool maskLowAmplitude = false,
    double amplitudeThreshold = 0.0)
        {
            if (_n <= 0) throw new InvalidOperationException("Keine Samples im LockInAccumulator.");

            int w = _w;
            int h = VisibleHeight;
            int nPix = PixelCount;

            double norm = normalize ? (2.0 / _n) : 1.0;

            var raw = new ushort[nPix];

            for (int i = 0; i < nPix; i++)
            {
                double I = norm * _I[i];
                double Q = norm * _Q[i];

                double amp = Math.Sqrt(I * I + Q * Q);
                if (maskLowAmplitude && amp < amplitudeThreshold)
                {
                    raw[i] = 0; // z.B. schwarz für unsichere Phase
                    continue;
                }

                double phi = Math.Atan2(Q, I); // [-pi..pi]
                double phi0to2pi = phi + Math.PI; // [0..2pi]

                // 0..2pi -> 0..65535
                raw[i] = (ushort)(phi0to2pi * (65535.0 / (2.0 * Math.PI)));
            }

            return new ThermalFrame(w, h, raw, gray8: null, metaU16: null, metaRows: 0);
        }




    }

    public sealed class LockInProgress
    {
        public TimeSpan Elapsed { get; set; }
        public TimeSpan Total { get; set; }
        public double Percent => Total.TotalMilliseconds <= 0 ? 0 : (Elapsed.TotalMilliseconds / Total.TotalMilliseconds) * 100.0;

        public int Frames { get; set; }
        public bool StimulusOn { get; set; }
        public double FrequencyHz { get; set; }
    }

}
