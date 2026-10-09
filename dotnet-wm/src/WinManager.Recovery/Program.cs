using System.Runtime.Versioning;
using WinManager.Windows;

[SupportedOSPlatform("windows10.0.15063")]
internal static class Program
{
    public static Task<int> Main(string[] args) => WindowsRecovery.RunAsync(args);
}