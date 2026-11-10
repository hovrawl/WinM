using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using WinManager.Ui;

namespace WinManager.DebugUi;

internal sealed class DebugApplication : Application
{
    public DebugApplication() => DeskTheme.Apply(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(Program.DemoMode, Program.PipeName);
            if (Program.SmokeMode)
            {
                var smokeExitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                smokeExitTimer.Tick += (_, _) =>
                {
                    smokeExitTimer.Stop();
                    desktop.Shutdown();
                };
                smokeExitTimer.Start();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}