using Avalonia;
#if DEBUG
using AvaloniaMcp.Diagnostics;
#endif

namespace WinManager.DebugUi;

internal static class Program
{
    public static bool DemoMode { get; private set; }
    public static string? PipeName { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        DemoMode = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var pipeArgument = Array.IndexOf(args, "--pipe");
        if (pipeArgument >= 0)
        {
            if (pipeArgument + 1 >= args.Length || string.IsNullOrWhiteSpace(args[pipeArgument + 1]))
                throw new ArgumentException("--pipe requires a pipe name.");
            PipeName = args[pipeArgument + 1];
        }
        var builder = AppBuilder.Configure<DebugApplication>().UsePlatformDetect();
#if DEBUG
        builder.UseMcpDiagnostics();
#endif
        builder.StartWithClassicDesktopLifetime(args);
    }
}
