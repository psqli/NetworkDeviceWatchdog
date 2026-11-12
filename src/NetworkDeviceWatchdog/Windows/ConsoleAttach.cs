using System.Runtime.InteropServices;

namespace NetworkDeviceWatchdog.App.Windows;

/// <summary>Lets the GUI (WinExe) build print to the console it was started from.</summary>
internal static partial class ConsoleAttach
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    /// <summary>
    /// Returns true when console output is available: either stdout is redirected
    /// (pipe or file) or the parent process's console could be attached.
    /// </summary>
    public static bool AttachToParent()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            return true;
        return AttachConsole(AttachParentProcess);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int handle);
}
