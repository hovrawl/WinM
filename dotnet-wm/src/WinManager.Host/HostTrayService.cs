using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;

namespace WinManager.Host;

internal interface IHostTray
{
    Task RunAsync(Action requestShutdown, CancellationToken stoppingToken);
}

internal sealed class HostTrayService(IHostApplicationLifetime lifetime, IHostTray tray) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        tray.RunAsync(lifetime.StopApplication, stoppingToken);
}

internal sealed class WindowsHostTray : IHostTray
{
    public Task RunAsync(Action requestShutdown, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(requestShutdown);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new ApplicationContext();
                using var dispatcher = new Control();
                _ = dispatcher.Handle;
                using var menu = new ContextMenuStrip();
                menu.Items.Add("Quit WinManager", null, (_, _) =>
                {
                    requestShutdown();
                    context.ExitThread();
                });
                using var icon = new NotifyIcon
                {
                    Icon = SystemIcons.Application,
                    Text = "WinManager",
                    ContextMenuStrip = menu,
                    Visible = true
                };
                using var registration = stoppingToken.Register(() =>
                {
                    if (dispatcher.IsDisposed || !dispatcher.IsHandleCreated) return;
                    try { dispatcher.BeginInvoke((Action)context.ExitThread); }
                    catch (InvalidOperationException) { }
                });

                Application.Run(context);
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        })
        {
            IsBackground = true,
            Name = "WinManager host tray"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
