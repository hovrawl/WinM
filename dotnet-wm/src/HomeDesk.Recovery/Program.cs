using System.Runtime.Versioning;
using HomeDesk.Windows;

namespace HomeDesk.Recovery;

[SupportedOSPlatform("windows10.0.15063")]
internal static class Program
{
    public static Task<int> Main(string[] args) => WindowsRecovery.RunAsync(args);
}