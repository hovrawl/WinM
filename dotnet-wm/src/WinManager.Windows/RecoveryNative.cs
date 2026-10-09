using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinManager.Windows;

internal sealed record RecoveryRecord(long Handle, uint ProcessId, ulong CreationTime, uint ShowCommand, string Property,
    bool HideConfirmed = false);
internal sealed record RecoveryJournal(uint HostId, ulong HostCreationTime, List<RecoveryRecord> Records);
internal sealed record RecoveryCommand(string Kind, RecoveryRecord? Record = null);

[SupportedOSPlatform("windows10.0.15063")]
internal static class RecoveryNative
{
    internal static bool TryProcessTime(uint pid, out ulong time)
    {
        time = 0;
        using var process = PInvoke.OpenProcess_SafeHandle(global::Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsInvalid || !PInvoke.GetProcessTimes(process, out var created, out var exited, out _, out _) ||
            exited.dwLowDateTime != 0 || exited.dwHighDateTime != 0) return false;
        time = ((ulong)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        return true;
    }

    internal static bool HostAlive(RecoveryJournal journal)
    {
        if (TryProcessTime(journal.HostId, out var time)) return time == journal.HostCreationTime;
        // An inaccessible live process is not proof of death.
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(checked((int)journal.HostId));
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    internal static unsafe bool Mark(RecoveryRecord record)
    {
        fixed (char* property = record.Property)
            return PInvoke.SetProp(new HWND((nint)record.Handle), property, new HANDLE((nint)1));
    }

    internal static bool Matches(RecoveryRecord record) => Verify(record) == true;

    private static unsafe bool? Verify(RecoveryRecord record)
    {
        var window = new HWND((nint)record.Handle);
        PInvoke.GetWindowThreadProcessId(window, out var pid);
        fixed (char* property = record.Property)
        {
            if (!PInvoke.IsWindow(window) || pid != record.ProcessId || (nint)PInvoke.GetProp(window, property).Value != 1)
                return false;
            if (!TryProcessTime(pid, out var time)) return null;
            return time == record.CreationTime;
        }
    }

    internal static bool Restore(RecoveryRecord record)
    {
        var identity = Verify(record);
        if (identity != true) return identity == false;
        var window = new HWND((nint)record.Handle);
        // A queued hide on a hung UI thread may not have executed yet. Seeing an already-visible
        // window is not proof that our subsequently queued show has executed.
        var canConfirm = record.HideConfirmed || !PInvoke.IsWindowVisible(window);
        var command = (SHOW_WINDOW_CMD)record.ShowCommand;
        var show = command == SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED ? SHOW_WINDOW_CMD.SW_SHOWNA : command;
        if (!PInvoke.ShowWindowAsync(window, show)) return false;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            identity = Verify(record);
            if (identity != true) return identity == false;
            var placement = new WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (canConfirm && PInvoke.IsWindowVisible(window) && PInvoke.GetWindowPlacement(window, ref placement) &&
                (command == SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE ? (bool)PInvoke.IsIconic(window) :
                    command == SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED ? placement.showCmd == SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED :
                    !PInvoke.IsIconic(window) && placement.showCmd != SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED))
                return true;
            Thread.Sleep(10);
        }
        return false;
    }

    internal static unsafe void Unmark(RecoveryRecord record)
    {
        fixed (char* property = record.Property)
            if (Matches(record)) PInvoke.RemoveProp(new HWND((nint)record.Handle), property);
    }
}