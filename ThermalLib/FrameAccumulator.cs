using System;

namespace ThermalCamLib
{
    public sealed class FrameAccumulator
    {
        private readonly int _w, _h, _metaRows;
        private readonly int _visibleH;
        private readonly int _nPix;
        private readonly ulong[] _sum;   // groß genug, damit nichts überläuft
        private int _count;

        public FrameAccumulator(int width, int height, int metaRows)
        {
            _w = width;
            _h = height;
            _metaRows = Math.Max(0, metaRows);
            _visibleH = Math.Max(0, _h - _metaRows);
            _nPix = _w * _visibleH;

            _sum = new ulong[_nPix];
        }

        public int Count => _count;
        public int Width => _w;
        public int HeightVisible => _visibleH;

        public void Add(ThermalFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.RawU16 == null) throw new InvalidOperationException("Frame.RawU16 fehlt.");
            if (frame.Width != _w || frame.Height != _h) throw new InvalidOperationException("Frame-Größe passt nicht.");
            if (frame.RawU16.Length != _w * _h) throw new InvalidOperationException("RawU16 Länge passt nicht.");

            // Nur sichtbarer Bereich (ohne Meta-Zeilen)
            for (int i = 0; i < _nPix; i++)
                _sum[i] += frame.RawU16[i];

            _count++;
        }

        /// <summary>
        /// Liefert ein ThermalFrame aus der Summe.
        /// normalizeToU16:
        /// - true  => skaliert Summe (Min/Max) auf 0..65535 (gut zum Anzeigen)
        /// - false => liefert Mittelwert (sum/count) als ushort (gut für weitere Verarbeitung)
        /// </summary>
        public ThermalFrame BuildFrame(bool normalizeToU16)
        {
            if (_count <= 0) throw new InvalidOperationException("Keine Frames akkumuliert.");

            ushort[] raw = new ushort[_nPix];

            if (!normalizeToU16)
            {
                // Mittelwert (physikalisch sinnvoll)
                for (int i = 0; i < _nPix; i++)
                    raw[i] = (ushort)(_sum[i] / (ulong)_count);

                return new ThermalFrame(_w, _visibleH, raw, gray8: null, metaU16: null, metaRows: 0);
            }

            // Summe min/max finden
            ulong min = ulong.MaxValue;
            ulong max = 0;
            for (int i = 0; i < _nPix; i++)
            {
                ulong v = _sum[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }

            ulong range = max > min ? (max - min) : 1UL;

            // Auf 0..65535 skalieren
            for (int i = 0; i < _nPix; i++)
            {
                ulong v = _sum[i];
                ulong f = (v - min) * 65535UL / range;
                raw[i] = (ushort)f;
            }

            return new ThermalFrame(_w, _visibleH, raw, gray8: null, metaU16: null, metaRows: 0);
        }


    }
}
