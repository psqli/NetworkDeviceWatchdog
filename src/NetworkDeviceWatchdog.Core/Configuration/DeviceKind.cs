namespace NetworkDeviceWatchdog.Configuration;

/// <summary>Device types and the checks each one gets by default.</summary>
public enum DeviceKind
{
    /// <summary>
    /// Receipt / kitchen printer speaking ESC/POS on a raw TCP port (9100):
    /// ping, MAC, real-time status (DLE EOT), stuck-job detection, Windows queue.
    /// </summary>
    EscPosPrinter,

    /// <summary>Office printer: ping, MAC, IPP printer state (port 631), Windows queue.</summary>
    Printer,

    /// <summary>Any networked device: ping, MAC, TCP ports, optional HTTP health check.</summary>
    Generic,
}

public static class DeviceKinds
{
    /// <summary>Names accepted in the configuration's <c>type</c> field.</summary>
    public static IReadOnlyList<string> Names { get; } = ["escpos-printer", "printer", "generic"];

    public static bool TryParse(string? value, out DeviceKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "escpos-printer" or "escpos" or "receipt-printer" or "thermal-printer":
                kind = DeviceKind.EscPosPrinter;
                return true;
            case "printer" or "ipp-printer" or "office-printer":
                kind = DeviceKind.Printer;
                return true;
            case "generic" or "device" or "embedded":
                kind = DeviceKind.Generic;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public static int DefaultPort(this DeviceKind kind) => kind switch
    {
        DeviceKind.EscPosPrinter => 9100,
        DeviceKind.Printer => 631,
        _ => 0,
    };
}
