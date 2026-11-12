using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NetworkDeviceWatchdog.Checks;

namespace NetworkDeviceWatchdog.Platform;

/// <summary>
/// Reads Windows print queues through the spooler API (<c>EnumJobs</c>, level 1)
/// and reports the first job that needs attention.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPrintQueueInspector : IPrintQueueInspector
{
    // JOB_STATUS_* flags
    private const uint Paused = 0x1, Error = 0x2, Offline = 0x20, PaperOut = 0x40,
        BlockedDevQ = 0x200, UserIntervention = 0x400;

    private const uint ProblemMask = Error | Offline | PaperOut | BlockedDevQ | UserIntervention;

    public PrintQueueProblem? FindProblem(string queueName)
    {
        if (!OpenPrinterW(queueName, out var printer, IntPtr.Zero))
            return null;   // queue missing or no access: nothing to report
        try
        {
            EnumJobsW(printer, 0, 255, 1, IntPtr.Zero, 0, out var needed, out _);
            if (needed == 0)
                return null;
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!EnumJobsW(printer, 0, 255, 1, buffer, needed, out _, out var count))
                    return null;
                var p = IntPtr.Size;
                var statusOffset = 7 * p;
                var stride = (7 * p + 5 * 4 + 16 + (p - 1)) & ~(p - 1);   // sizeof(JOB_INFO_1W)
                for (var i = 0; i < count; i++)
                {
                    var entry = buffer + i * stride;
                    var status = (uint)Marshal.ReadInt32(entry, statusOffset);
                    if ((status & ProblemMask) == 0)
                        continue;
                    var jobId = Marshal.ReadInt32(entry, 0);
                    var document = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, 4 * p)) ?? "";
                    var statusText = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, 6 * p));
                    return new PrintQueueProblem(jobId, document, string.IsNullOrEmpty(statusText) ? Describe(status) : statusText);
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(printer);
        }
    }

    private static string Describe(uint status)
    {
        var names = new List<string>();
        if ((status & Error) != 0) names.Add("Error");
        if ((status & Offline) != 0) names.Add("Offline");
        if ((status & PaperOut) != 0) names.Add("PaperOut");
        if ((status & BlockedDevQ) != 0) names.Add("Blocked");
        if ((status & UserIntervention) != 0) names.Add("UserIntervention");
        if ((status & Paused) != 0) names.Add("Paused");
        return string.Join(", ", names);
    }

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinterW(string printerName, out IntPtr printer, IntPtr defaults);

    [LibraryImport("winspool.drv", EntryPoint = "EnumJobsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumJobsW(IntPtr printer, uint firstJob, uint numberOfJobs, uint level,
        IntPtr jobs, uint bufferSize, out uint needed, out uint returned);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(IntPtr printer);
}
