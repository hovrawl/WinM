using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WinManager.Ui;

namespace WinManager.SpatialSwitcher;

internal sealed class SpatialApplication : Application
{
    private WindowIcon? trayWindowIcon;

    public SpatialApplication()
    {
        DeskTheme.Apply(this);
        SwitcherTheme.Initialize(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var connectionStatus = new NativeMenuItem("HomeDeskSpace disconnected") { IsEnabled = false };
            var shortcutStatus = new NativeMenuItem("Shortcuts unavailable") { IsEnabled = false };
            var switcher = new SwitcherWindow(Program.DemoMode, Program.PipeName,
                (connection, shortcuts) =>
                {
                    connectionStatus.Header = connection;
                    shortcutStatus.Header = shortcuts;
                });

            var menu = new NativeMenu();
            menu.Items.Add(connectionStatus);
            menu.Items.Add(shortcutStatus);
            menu.Items.Add(new NativeMenuItemSeparator());

            var appearanceMenu = new NativeMenu();
            var appearanceItems = new Dictionary<SwitcherThemeMode, NativeMenuItem>();

            void ApplyTheme(SwitcherThemeMode mode)
            {
                SwitcherTheme.SetPreference(this, mode);
                foreach (var (itemMode, themeItem) in appearanceItems)
                    themeItem.IsChecked = itemMode == mode;
            }

            foreach (var mode in Enum.GetValues<SwitcherThemeMode>())
            {
                var item = new NativeMenuItem(mode.ToString())
                {
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = mode == SwitcherTheme.Preference
                };
                item.Click += (_, _) => { ApplyTheme(mode); };
                appearanceItems.Add(mode, item);
                appearanceMenu.Items.Add(item);
            }

            menu.Items.Add(new NativeMenuItem("Appearance") { Menu = appearanceMenu });
            var settings = new NativeMenuItem("Settings…");
            settings.Click += (_, _) => switcher.ShowSettingsWindow(this, ApplyTheme);
            menu.Items.Add(settings);
            menu.Items.Add(new NativeMenuItemSeparator());

            var showWindows = new NativeMenuItem("Show window switcher");
            showWindows.Click += (_, _) => switcher.ShowWindowSwitcher();
            menu.Items.Add(showWindows);
            var showCarousel = new NativeMenuItem("Show preview carousel · Ctrl+Alt+Space");
            showCarousel.Click += async (_, _) => await switcher.ShowCarouselExperimentAsync();
            menu.Items.Add(showCarousel);
            var showWorkspaces = new NativeMenuItem("Show workspace switcher");
            showWorkspaces.Click += (_, _) => switcher.ShowWorkspaceSwitcher();
            menu.Items.Add(showWorkspaces);

            var reconnect = new NativeMenuItem("Reconnect to HomeDeskSpace");
            reconnect.Click += (_, _) => _ = switcher.ConnectAsync();
            menu.Items.Add(reconnect);
            menu.Items.Add(new NativeMenuItemSeparator());

            var quit = new NativeMenuItem("Quit");
            quit.Click += async (_, _) => await QuitAsync(desktop, switcher);
            menu.Items.Add(quit);

            var trayIcon = new TrayIcon
            {
                Icon = CreateTrayIcon(),
                ToolTipText = "HomeDeskManager",
                Menu = menu
            };
            var trayIcons = new TrayIcons();
            trayIcons.Add(trayIcon);
            TrayIcon.SetIcons(this, trayIcons);

            switcher.Initialize();
            if (Program.SmokeMode)
            {
                switcher.ShowWorkspaceSwitcher();
                var smokeExitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                smokeExitTimer.Tick += async (_, _) =>
                {
                    smokeExitTimer.Stop();
                    await QuitAsync(desktop, switcher);
                };
                smokeExitTimer.Start();
            }

            desktop.Exit += (_, _) => TrayIcon.SetIcons(this, new TrayIcons());
        }

        base.OnFrameworkInitializationCompleted();
    }

    private WindowIcon CreateTrayIcon()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#435A4A")), null, new Rect(0, 0, 32, 32), 6, 6);
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#F5F1E8")), null, new Rect(6, 7, 20, 17), 2, 2);
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#A2B5A5")), null, new Rect(10, 11, 12, 2));
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#A2B5A5")), null, new Rect(10, 16, 12, 2));
        }

        trayWindowIcon = new WindowIcon(bitmap);
        return trayWindowIcon;
    }

    private async Task QuitAsync(IClassicDesktopStyleApplicationLifetime desktop, SwitcherWindow switcher)
    {
        await switcher.PrepareForShutdownAsync();
        TrayIcon.SetIcons(this, new TrayIcons());
        trayWindowIcon = null;
        desktop.Shutdown();
    }
}