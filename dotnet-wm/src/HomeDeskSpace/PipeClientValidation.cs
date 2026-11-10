using System.IO.Pipes;
using System.Security.Principal;
using WinManager.Windows;

namespace WinManager.Host;

internal static class PipeClientValidation
{
    internal static bool Validate(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!LocalPipeClient.IsCurrentSession(pipe.SafePipeHandle)) return false;
        using var host = WindowsIdentity.GetCurrent();
        var matches = false;
        pipe.RunAsClient(() =>
        {
            using var client = WindowsIdentity.GetCurrent(true);
            matches = host.User is not null && client?.User is not null && host.User == client.User;
        });
        return matches;
    }
}