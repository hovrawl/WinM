using System.IO.Pipes;
using System.Runtime.InteropServices;
using WinManager.Windows;
using Xunit;

namespace WinManager.Ipc.Tests;

public sealed class LocalClientValidationTests
{
    [Fact]
    public async Task SameProcessSameSessionClientIsAcceptedByNativeValidation()
    {
        var name = "WinManager.SecurityTest." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await accepting;
        Assert.True(LocalPipeClient.IsCurrentSession(server.SafePipeHandle, out var detail),
            $"Local native validation rejected the current process: {detail}; Win32 error {Marshal.GetLastPInvokeError()}.");
    }
}