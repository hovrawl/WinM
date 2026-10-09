using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinManager.Core;
using WinManager.Windows;

if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
    throw new PlatformNotSupportedException("WinManager requires Windows 10 build 15063 or newer.");

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IDesktopObserver, WindowsDesktopObserver>();
builder.Services.AddSingleton<DesktopObservation>();
builder.Services.AddHostedService<ObservationService>();
using var host = builder.Build();
await host.RunAsync();

internal sealed class ObservationService(DesktopObservation observation,
    IHostApplicationLifetime lifetime, ILogger<ObservationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        observation.Changed += snapshot => logger.LogInformation(
            "Observed {WindowCount} eligible windows on {MonitorCount} monitors; focus {Focus}",
            snapshot.Windows.Count, snapshot.Monitors.Count, snapshot.FocusedWindowId);
        try { await observation.RunAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { lifetime.StopApplication(); }
    }
}