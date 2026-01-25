namespace ShellyNet
{
    public enum ShellyGeneration
    {
        Unknown = 0,
        Gen1 = 1,
        Gen2 = 2
    }

    public sealed class ShellyDevice
    {
        public string Name { get; set; }        // DisplayName
        public string Id { get; set; }          // Zeroconf Id (statt HostName)
        public string IpAddress { get; set; }
        public int Port { get; set; }
        public ShellyGeneration Generation { get; set; }

        public string Model { get; set; }
        public string DeviceId { get; set; }
    }

}
