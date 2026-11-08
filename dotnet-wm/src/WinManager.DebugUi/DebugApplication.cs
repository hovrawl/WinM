using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace WinManager.DebugUi;

internal sealed class DebugApplication : Application
{
    public DebugApplication() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Program.DemoMode, Program.PipeName);
        base.OnFrameworkInitializationCompleted();
    }
}
