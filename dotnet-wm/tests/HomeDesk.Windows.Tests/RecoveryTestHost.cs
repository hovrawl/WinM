using System.Runtime.Versioning;
using WinManager.Core;

namespace WinManager.Windows.Tests;

[SupportedOSPlatform("windows10.0.15063")]
internal static class RecoveryTestHost
{
    internal static async Task<int> RefuseAcknowledgmentAsync(string pipeName)
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(timeout.Token);
        await RecoveryProtocol.WriteAsync(pipe, "READY", timeout.Token);
        await RecoveryProtocol.ReadAsync(pipe, timeout.Token);
        // Deliberately exit after receiving a registration without acknowledging or hiding anything.
        return 0;
    }

    internal static async Task<int> RunAsync(string title, string recoveryExecutable)
    {
        await using var observer = new WindowsDesktopObserver(recoveryExecutablePath: recoveryExecutable);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await observer.StartAsync(timeout.Token);
        var window = (await observer.CaptureAsync(timeout.Token)).Windows.Single(window => window.Title == title);
        var result = await observer.ApplyAsync(new LayoutPlan([], Visibility: [new(window.Id, false)]), timeout.Token);
        if (result.Failures.Count != 0)
        {
            Console.WriteLine(string.Join(",", result.Failures.Select(failure => failure.Code + ":" + failure.Message)));
            return 1;
        }
        Console.WriteLine("HIDDEN");
        Console.WriteLine(observer.RecoveryHelperProcessId);
        await Console.In.ReadLineAsync();
        return 0;
    }
}