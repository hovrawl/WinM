using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using WinManager.Ui;

namespace WinManager.SpatialSwitcher;

internal sealed class SpatialApplication : Application
{
    public SpatialApplication() => DeskTheme.Apply(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new SwitcherWindow(Program.DemoMode, Program.PipeName);
        base.OnFrameworkInitializationCompleted();
    }
}