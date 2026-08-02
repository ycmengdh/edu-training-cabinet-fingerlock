using System.IO.Ports;
using Microsoft.Win32;

namespace CabinetLock
{
    public static class SerialPortDiscovery
    {
        private sealed record UsbPortIdentity(string PortName, string HardwareKey, string FriendlyName);

        public static IReadOnlyList<string> GetPortNames()
        {
            string[] ports;
            try
            {
                ports = SerialPort.GetPortNames();
            }
            catch
            {
                return Array.Empty<string>();
            }

            HashSet<string> bluetoothPorts = ReadBluetoothPorts();
            return ports.Where(port => !string.IsNullOrWhiteSpace(port))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(port => bluetoothPorts.Contains(port))
                .ThenBy(GetPortNumber)
                .ThenBy(port => port, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static string? GetPreferredPortName(bool directUart)
        {
            IReadOnlyList<string> ports = GetPortNames();
            if (ports.Count == 0) return null;

            Dictionary<string, UsbPortIdentity> identities = ReadUsbPortIdentities();
            return ports
                .OrderBy(port => GetModeScore(port, directUart, identities))
                .ThenBy(GetPortNumber)
                .FirstOrDefault();
        }

        public static string GetPortDescription(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName)) return "";
            Dictionary<string, UsbPortIdentity> identities = ReadUsbPortIdentities();
            return identities.TryGetValue(portName.Trim(), out UsbPortIdentity? identity) &&
                   !string.IsNullOrWhiteSpace(identity.FriendlyName)
                ? identity.FriendlyName
                : portName.Trim();
        }

        private static int GetModeScore(
            string port,
            bool directUart,
            IReadOnlyDictionary<string, UsbPortIdentity> identities)
        {
            if (!identities.TryGetValue(port, out UsbPortIdentity? identity))
                return ReadBluetoothPorts().Contains(port) ? 90 : 40;

            string hardware = identity.HardwareKey;
            bool espNative = hardware.Contains("VID_303A&PID_1001", StringComparison.OrdinalIgnoreCase);
            bool uartAdapter = hardware.Contains("VID_1A86", StringComparison.OrdinalIgnoreCase) ||
                               hardware.Contains("VID_0403", StringComparison.OrdinalIgnoreCase) ||
                               hardware.Contains("VID_10C4", StringComparison.OrdinalIgnoreCase) ||
                               hardware.Contains("VID_067B", StringComparison.OrdinalIgnoreCase);

            if (directUart)
            {
                if (hardware.Contains("VID_1A86&PID_55D3", StringComparison.OrdinalIgnoreCase)) return 0;
                if (uartAdapter) return 10;
                if (espNative) return 60;
                return 30;
            }

            if (espNative) return 0;
            if (uartAdapter) return 50;
            return 30;
        }

        private static Dictionary<string, UsbPortIdentity> ReadUsbPortIdentities()
        {
            var result = new Dictionary<string, UsbPortIdentity>(StringComparer.OrdinalIgnoreCase);
            if (!OperatingSystem.IsWindows()) return result;

            try
            {
                using RegistryKey? usb = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\USB");
                if (usb == null) return result;

                foreach (string hardwareKey in usb.GetSubKeyNames())
                {
                    using RegistryKey? hardware = usb.OpenSubKey(hardwareKey);
                    if (hardware == null) continue;
                    foreach (string instanceName in hardware.GetSubKeyNames())
                    {
                        using RegistryKey? instance = hardware.OpenSubKey(instanceName);
                        using RegistryKey? parameters = instance?.OpenSubKey("Device Parameters");
                        string portName = parameters?.GetValue("PortName") as string ?? "";
                        if (string.IsNullOrWhiteSpace(portName)) continue;
                        string friendlyName = instance?.GetValue("FriendlyName") as string ?? portName;
                        result[portName] = new UsbPortIdentity(portName, hardwareKey, friendlyName);
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        private static HashSet<string> ReadBluetoothPorts()
        {
            var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DEVICEMAP\SERIALCOMM");
                if (key == null) return ports;
                foreach (string valueName in key.GetValueNames().Where(name =>
                             name.Contains("BthModem", StringComparison.OrdinalIgnoreCase)))
                {
                    if (key.GetValue(valueName) is string port && !string.IsNullOrWhiteSpace(port))
                        ports.Add(port);
                }
            }
            catch
            {
            }
            return ports;
        }

        private static int GetPortNumber(string port) =>
            int.TryParse(new string(port.SkipWhile(character => !char.IsDigit(character)).ToArray()),
                out int number)
                ? number
                : int.MaxValue;
    }
}
