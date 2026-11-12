using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HomeDesk.Client;

internal static class ForegroundActivation
{
    internal static bool TryAllowForegroundActivation(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            if (!pipe.IsConnected) return false;
            var handle = pipe.SafePipeHandle;
            if (handle.IsClosed || handle.IsInvalid || !GetNamedPipeServerProcessId(handle, out var processId) ||
                processId == 0)
            {
                return false;
            }

            return AllowSetForegroundWindow(processId);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}