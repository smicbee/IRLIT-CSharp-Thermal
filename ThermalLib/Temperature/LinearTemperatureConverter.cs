using System;

namespace ThermalCamLib.Temperature
{
    /// <summary>
    /// Simpler Fallback: linear zwischen Raw-Min/Max auf Temperaturbereich abbilden.
    /// Nicht "wissenschaftlich korrekt", aber praktisch für erste Tests.
    /// </summary>
    public sealed class LinearTemperatureConverter : ITemperatureConverter
    {
        private readonly ushort _rawMin;
        private readonly ushort _rawMax;
        private readonly double _tMin;
        private readonly double _tMax;

        public LinearTemperatureConverter(ushort rawMin, ushort rawMax, double tMinC, double tMaxC)
        {
            if (rawMax <= rawMin) throw new ArgumentException("rawMax muss > rawMin sein.");
            _rawMin = rawMin;
            _rawMax = rawMax;
            _tMin = tMinC;
            _tMax = tMaxC;
        }

        public double RawToCelsius(ushort raw)
        {
            var clamped = Math.Min(_rawMax, Math.Max(_rawMin, raw));
            var f = (clamped - _rawMin) / (double)(_rawMax - _rawMin);
            return _tMin + f * (_tMax - _tMin);
        }

        public void UpdateFromMeta(ushort[] metaU16)
        {
            // No-op im Linear-Fallback.
        }
    }
}
