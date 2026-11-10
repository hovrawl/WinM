using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using WinManager.Contracts;
using WinManager.Core;
using WinManager.Host;
using WinManager.Windows;

var dataDirectory = args is ["--tray-smoke"] or ["--recovery-smoke"] or ["--recovery", "--smoke-directory", ..]
    ? AppContext.BaseDirectory
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinManager");
try
{
    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
        throw new PlatformNotSupportedException("HomeDeskSpace requires Windows 10 build 15063 or newer.");
    if (args.Length > 0 && args[0] == "--recovery")
    {
        if (args.Length > 2 && args[1] == "--smoke-directory")
            return await WindowsRecovery.RunAsync(args[3..], args[2]);
        return await WindowsRecovery.RunAsync(args[1..]);
    }

    if (args is ["--tray-smoke"])
    {
        var smokeBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        smokeBuilder.Logging.ClearProviders();
        smokeBuilder.Services.AddSingleton<HostTrayVisibility>();
        smokeBuilder.Services.AddSingleton<IHostTray, WindowsHostTray>();
        smokeBuilder.Services.AddHostedService<HostTrayService>();
        using var smokeHost = smokeBuilder.Build();
        using var smokeStop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await smokeHost.RunAsync(smokeStop.Token);
        return 0;
    }

    if (args is ["--recovery-smoke"])
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "recovery-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var recoveryLaunch = HostRecoveryLaunch.Resolve(null, Environment.ProcessPath, useSelfExecutable: true);
            WindowsRecovery.CheckHelperLaunch(recoveryLaunch.Executable,
                recoveryLaunch.ArgumentPrefix.Concat(["--smoke-directory", directory]).ToArray());
            return 0;
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    using var process = System.Diagnostics.Process.GetCurrentProcess();
    if (process.SessionId == 0)
        throw new PlatformNotSupportedException("Run in the interactive user session, not Session 0.");
    string? configPath = null;
    string? pipeName = null;
    string? recoveryPath = null;
    for (var index = 0; index < args.Length; index++)
    {
        if (args[index] == "--help")
        {
            Console.WriteLine(
                "HomeDeskSpace [--config PATH] [--data-dir PATH] [--pipe NAME] [--recovery-executable PATH]");
            return 0;
        }

        if (index + 1 >= args.Length) throw new ArgumentException("Host option requires a value.");
        switch (args[index++])
        {
            case "--config": configPath = Path.GetFullPath(args[index]); break;
            case "--data-dir": dataDirectory = Path.GetFullPath(args[index]); break;
            case "--pipe": pipeName = args[index]; break;
            case "--recovery-executable": recoveryPath = Path.GetFullPath(args[index]); break;
            default: throw new ArgumentException("Unknown host option.");
        }
    }

    using var instance = new SingleInstanceLease("Local\\" + Protocol.DefaultPipeName() + ".instance");
    if (!instance.Acquired) return 5;
    var configurationPath = Path.GetFullPath(configPath ?? Path.Combine(dataDirectory, "config.json"));
    var configuration = ManagerConfiguration.Load(configurationPath, createDefault: configPath is null);
    var configurationStore = new ConfigurationStore(configurationPath);
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(new RotatingFileLoggerProvider(Path.Combine(dataDirectory, "Logs"),
        configuration.LogMaximumBytes, configuration.LogFileCount));
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(20));
    builder.Services.AddSingleton(configuration);
    builder.Services.AddSingleton(configurationStore);
    builder.Services.AddSingleton<HostRuntimeState>();
    builder.Services.AddSingleton<HostTrayVisibility>();
    builder.Services.AddSingleton(configuration.Touchpad);
    builder.Services.AddSingleton<TouchpadInputSource>();
    builder.Services.AddSingleton<IHostTray, WindowsHostTray>();
    HostComposition.AddNativeServices(builder.Services, configuration, recoveryPath);
    builder.Services.AddSingleton(services => new ManagerEngine(services.GetRequiredService<IWindowPlatform>(),
        configuration.InnerGap, configuration.OuterGap, configuration.InputCapacity, configuration.Workspaces,
        configuration.MonitorOrder));
    builder.Services.AddSingleton(services => new PipeServer(services.GetRequiredService<ManagerEngine>(), pipeName,
        configuration.MaximumClients, configuration.OutputCapacity, configurationStore,
        services.GetRequiredService<IWindowPlatform>() as IPreviewTargetResolver,
        services.GetRequiredService<HostTrayVisibility>()));
    builder.Services.AddHostedService<HostTrayService>();
    builder.Services.AddHostedService<ObservationService>();
    builder.Services.AddHostedService<PipeService>();
    builder.Services.AddHostedService<ShortcutService>();
    builder.Services.AddHostedService<GestureService>();
    using var host = builder.Build();
    await host.StartAsync();
    await host.WaitForShutdownAsync();
    return host.Services.GetRequiredService<HostRuntimeState>().Failed ? 2 :
        host.Services.GetRequiredService<ManagerEngine>().ShutdownFailures.Count == 0 ? 0 : 6;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    try
    {
        using var emergency = new RotatingFileLoggerProvider(Path.Combine(dataDirectory, "Logs"), 2 * 1024 * 1024, 4);
        emergency.CreateLogger("Startup").LogError(error, "Host startup or lifetime failed");
    }
    catch (Exception)
    {
    }

    return 2;
}

[SupportedOSPlatform("windows10.0.15063")]
internal static class HostComposition
{
    public static void AddNativeServices(IServiceCollection services, ManagerConfiguration configuration,
        string? recoveryPath)
    {
        var recoveryLaunch = HostRecoveryLaunch.Resolve(recoveryPath, Environment.ProcessPath,
            HostBuild.IsNativeAot);
        services.AddSingleton(_ => new WindowsDesktopObserver(configuration.IgnoredProcessNames,
            configuration.IgnoredClassNames, recoveryLaunch.Executable, recoveryLaunch.ArgumentPrefix));
        services.AddSingleton<IWindowPlatform>(provider => provider.GetRequiredService<WindowsDesktopObserver>());
    }
}

internal static class HostBuild
{
#if WINMANAGER_HOST_AOT
    public const bool IsNativeAot = true;
#else
    public const bool IsNativeAot = false;
#endif
}

internal sealed record RecoveryLaunch(string Executable, IReadOnlyList<string> ArgumentPrefix);

internal static class HostRecoveryLaunch
{
    public static RecoveryLaunch Resolve(string? configuredPath, string? processPath, bool useSelfExecutable)
    {
        if (configuredPath is not null) return new RecoveryLaunch(configuredPath, []);
        if (useSelfExecutable)
            return new RecoveryLaunch(
                processPath ?? throw new InvalidOperationException("The host executable path is unavailable."),
                ["--recovery"]);
        return new RecoveryLaunch(Path.Combine(AppContext.BaseDirectory, "WinManager.Recovery.exe"), []);
    }
}

internal sealed class HostRuntimeState
{
    private int failed;
    public bool Failed => Volatile.Read(ref failed) != 0;
    public void MarkFailed() => Interlocked.Exchange(ref failed, 1);
}

internal sealed class ObservationService(
    ManagerEngine observation,
    IHostApplicationLifetime lifetime,
    ILogger<ObservationService> logger,
    HostRuntimeState status) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        observation.Changed += snapshot => logger.LogInformation(
            "Observed {WindowCount} eligible windows on {MonitorCount} monitors; focus {Focus}",
            snapshot.Desktop.Windows.Count, snapshot.Desktop.Monitors.Count, snapshot.Desktop.FocusedWindowId);
        observation.ActionsFailed += failures =>
        {
            foreach (var failure in failures)
                logger.LogWarning("Window action diagnostic: {Code} {WindowId} {Message}", failure.Code,
                    failure.WindowId, failure.Message);
        };
        try
        {
            await observation.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            status.MarkFailed();
            throw;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}

internal sealed class PipeService(PipeServer server, ManagerEngine manager, HostRuntimeState status) : BackgroundService
{
    private readonly CancellationTokenSource serverStop = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await server.RunAsync(serverStop.Token);
        }
        catch (OperationCanceledException) when (serverStop.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            status.MarkFailed();
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await manager.Stopped.WaitAsync(cancellationToken);
        }
        finally
        {
            await serverStop.CancelAsync();
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        serverStop.Dispose();
        base.Dispose();
    }
}

[SupportedOSPlatform("windows10.0.15063")]
internal sealed class ShortcutService(
    WindowsDesktopObserver platform,
    ManagerEngine manager,
    ManagerConfiguration configuration,
    ILogger<ShortcutService> logger,
    HostRuntimeState status) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await platform.StartAsync(stoppingToken);
            await manager.Ready.WaitAsync(stoppingToken);
            var failures = await platform.RegisterShortcutsAsync(configuration.BuildShortcutBindings(), stoppingToken);
            if (failures.Count > 0)
                throw new InvalidOperationException(string.Join("; ", failures.Select(failure => failure.Message)));
            await foreach (var command in platform.ShortcutCommands(stoppingToken))
            {
                var result = await manager.ExecuteAsync(command, stoppingToken);
                if (!result.Success)
                    logger.LogWarning("Shortcut rejected: {Code} {Message}", result.ErrorCode, result.Message);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            status.MarkFailed();
            throw;
        }
    }
}