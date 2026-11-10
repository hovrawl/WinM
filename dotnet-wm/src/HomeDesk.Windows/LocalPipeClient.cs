using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace WinManager.Windows;

[SupportedOSPlatform("windows6.0.6000")]
public static class LocalPipeClient
{
    public static bool IsCurrentSession(SafePipeHandle pipe) => IsCurrentSession(pipe, out _);

    public static bool IsCurrentSession(SafePipeHandle pipe, out string detail)
    {
        detail = "Unable to read client session.";
        if (!PInvoke.GetNamedPipeClientSessionId(pipe, out var session)) return false;
        using var current = Process.GetCurrentProcess();
        detail = $"Client session {session}; server session {current.SessionId}.";
        if (session != current.SessionId) return false;
        detail = "Unable to read client process.";
        if (!PInvoke.GetNamedPipeClientProcessId(pipe, out var processId) || processId == 0 || processId > int.MaxValue)
            return false;
        try
        {
            using var client = Process.GetProcessById((int)processId);
            detail = $"Client process {processId}, session {client.SessionId}; pipe session {session}.";
            return !client.HasExited && client.SessionId == session;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            detail = $"Unable to verify client process: {exception.Message}";
            return false;
        }
    }
}