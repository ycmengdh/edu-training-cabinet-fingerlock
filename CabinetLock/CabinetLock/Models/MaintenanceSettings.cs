namespace CabinetLock
{
    public sealed class MaintenanceSettings
    {
        public const string DefaultPin = "112233";
        public const int PinLength = 6;
        public const string DevicePinEncoding = "digit_plus_one";
        public const uint DevicePinEncodingMinFirmware = 26081007;

        public string Pin { get; init; } = DefaultPin;
        public uint Version { get; init; } = 1;
        public DateTime UpdateTime { get; init; } = DateTime.Now;

        public static bool IsValidPin(string? pin) =>
            pin?.Length == PinLength && pin.All(character => character is >= '1' and <= '4');

        public static string EncodeForDevice(string pin)
        {
            if (!IsValidPin(pin))
                throw new ArgumentException("维护密码必须是由按键 1-4 组成的 6 位密码", nameof(pin));
            char[] encoded = pin.ToCharArray();
            for (int index = 0; index < encoded.Length; index++)
                encoded[index] = (char)(encoded[index] + 1);
            return new string(encoded);
        }

        public static bool SupportsDevicePinEncoding(string? firmwareVersion)
        {
            if (string.IsNullOrWhiteSpace(firmwareVersion)) return false;
            string version = firmwareVersion.Trim();
            int separator = version.IndexOf('-');
            if (separator >= 0) version = version[..separator];
            return uint.TryParse(version, out uint numericVersion) &&
                   numericVersion >= DevicePinEncodingMinFirmware;
        }
    }
}
