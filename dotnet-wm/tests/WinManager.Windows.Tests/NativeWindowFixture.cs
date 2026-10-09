using System.Diagnostics;
using System.Runtime.Versioning;
using Xunit;

namespace WinManager.Windows.Tests;

[SupportedOSPlatform("windows10.0.15063")]
internal sealed class NativeWindowFixture : IAsyncDisposable
{
    private readonly Process process;
    private Task<string> errors = Task.FromResult("");
    private bool hasStarted;
    private bool disposed;
    public string Prefix { get; } = $"WinManager.Test.{Guid.NewGuid():N}";
    public string WindowClass => Prefix + ".Window";
    public string IgnoredClass => Prefix + ".Ignored";
    public int ProcessId => process.Id;

    public static async Task<NativeWindowFixture> StartAsync(bool layoutOnly = false)
    {
        if (Environment.GetEnvironmentVariable("WINMANAGER_ISOLATED_INTERACTIVE") != "1")
            throw new InvalidOperationException("Native fixtures require explicit isolated-desktop opt-in.");
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var process = new Process { StartInfo = info };
        info.Environment["WINMANAGER_TEST_FOREGROUND_PARENT"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var fixture = new NativeWindowFixture(process, info);
        if (layoutOnly) info.ArgumentList[1] = "--native-layout-fixture";
        try
        {
            Assert.True(process.Start());
            fixture.hasStarted = true;
            NativeInspection.GrantForeground((uint)process.Id);
            fixture.errors = process.StandardError.ReadToEndAsync();
            Assert.Equal("READY", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private NativeWindowFixture(Process process, ProcessStartInfo info)
    {
        this.process = process;
        info.ArgumentList.Add(typeof(TestWindowProgram).Assembly.Location);
        info.ArgumentList.Add("--native-window-fixture");
        info.ArgumentList.Add(Prefix);
    }

    public async Task CommandAsync(string command)
    {
        await process.StandardInput.WriteLineAsync(command);
        await process.StandardInput.FlushAsync();
        var response = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("OK", response);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!hasStarted) return;
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            var errorText = await errors;
            Assert.True(process.ExitCode == 0, errorText);
        }
        finally { process.Dispose(); }
    }
}