namespace ThermalCamLib.Temperature
{
    public interface ITemperatureConverter
    {
        /// <summary>
        /// Wandelt einen RawU16-Wert in °C um.
        /// </summary>
        double RawToCelsius(ushort raw);

        /// <summary>
        /// Optional: falls du aus Meta/„4 line data“ eine LUT bauen willst.
        /// </summary>
        void UpdateFromMeta(ushort[] metaU16);
    }
}
