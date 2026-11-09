using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using WinManager.Ui;

namespace WinManager.DebugUi;

internal sealed class DebugApplication : Application
{
    public DebugApplication() => DeskTheme.Apply(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Program.DemoMode, Program.PipeName);
        base.OnFrameworkInitializationCompleted();
    }
}