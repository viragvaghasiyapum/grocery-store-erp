using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace GroceryErp.App;

internal sealed record ScannerComPort(string Name, bool IsBluetoothVirtual);

internal static class ScannerComPorts
{
    // Windows updates this map as serial devices are added and removed.
    internal static ScannerComPort[] GetPorts()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is null) return [];
            return key.GetValueNames()
                .Select(name => new ScannerComPort(key.GetValue(name) as string ?? "",
                    name.StartsWith(@"\Device\BthModem", StringComparison.OrdinalIgnoreCase)))
                .Where(static port => IsComPort(port.Name))
                .DistinctBy(static port => port.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static port => int.Parse(port.Name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }

    internal static bool IsComPort(string? port) => port is { Length: > 3 } &&
        port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(port.AsSpan(3), out int number) && number > 0;
}
