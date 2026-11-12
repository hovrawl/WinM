using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HomeDesk.Contracts;
using HomeDesk.Ui;

namespace HomeDeskManager;

internal sealed class GeneralSettingsWindow : Window
{
    private readonly bool demoMode;
    private readonly Action<SwitcherThemeMode> themeChanged;
    private readonly ComboBox theme = new() { MinWidth = 160 };
    private readonly TextBox innerGap = new() { Width = 100 };
    private readonly TextBox outerGap = new() { Width = 100 };
    private readonly CheckBox defaultShortcuts = new() { Content = "Enable default shortcuts" };
    private readonly CheckBox touchpad = new() { Content = "Enable touchpad gestures" };
    private readonly StackPanel monitorMappings = new() { Spacing = 6 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button reloadButton;
    private readonly Button saveButton;
    private readonly Button identifyMonitorsButton;
    private readonly List<Button> mappingButtons = [];
    private readonly List<Window> monitorIdentificationWindows = [];
    private ManagerSession? session;
    private ConfigurationDocument? document;
    private ManagerSnapshot? snapshot;
    private IReadOnlyList<string> configuredMonitorOrder = [];
    private List<string> monitorOrder = [];
    private CancellationTokenSource? identificationLifetime;
    private bool busy;
    private bool loading;
    private bool dirty;

    public GeneralSettingsWindow(Application application, ManagerSession? session, bool demoMode,
        Action<SwitcherThemeMode> themeChanged)
    {
        this.demoMode = demoMode;
        this.themeChanged = themeChanged;
        Title = "HomeDeskManager — Settings";
        Width = 620;
        Height = 720;
        MinWidth = 500;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SwitcherTheme.Canvas;
        Foreground = SwitcherTheme.Text;

        theme.ItemsSource = Enum.GetValues<SwitcherThemeMode>();
        theme.SelectedItem = SwitcherTheme.Preference;
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is SwitcherThemeMode mode)
            {
                SwitcherTheme.SetPreference(application, mode);
                this.themeChanged(mode);
            }
        };
        innerGap.TextChanged += (_, _) => MarkDirty();
        outerGap.TextChanged += (_, _) => MarkDirty();
        defaultShortcuts.Click += (_, _) => MarkDirty();
        touchpad.Click += (_, _) => MarkDirty();
        identifyMonitorsButton = SwitcherTheme.Action("Identify monitors");
        identifyMonitorsButton.Click += async (_, _) => await IdentifyMonitorsAsync();

        reloadButton = SwitcherTheme.Action("Reload");
        reloadButton.Click += async (_, _) => await ReloadAsync();
        saveButton = SwitcherTheme.Action("Save settings", primary: true);
        saveButton.Click += async (_, _) => await SaveAsync();
        Content = BuildContent();
        Closed += (_, _) => CloseMonitorIdentifications();
        UpdateSession(session);
    }

    public void UpdateSnapshot(ManagerSnapshot? nextSnapshot)
    {
        snapshot = nextSnapshot;
        if (!dirty)
        {
            monitorOrder = BuildMonitorOrder(configuredMonitorOrder, snapshot);
            RenderMonitorMappings();
        }

        UpdatePresentation();
    }

    public void UpdateSession(ManagerSession? activeSession)
    {
        session = activeSession;
        if (activeSession is null)
        {
            if (document is null)
                status.Text = demoMode
                    ? "Demo mode uses sample data. Connect to HomeDeskSpace to read or save its settings."
                    : "Connect to HomeDeskSpace to read or save its settings.";
            UpdatePresentation();
            return;
        }

        if (dirty && document is not null)
            status.Text = "Reconnected. Your staged changes were kept; save or reload explicitly.";
        else
            _ = ReloadAsync();
        UpdatePresentation();
    }

    private Control BuildContent()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions
                { new(GridLength.Auto), new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) },
            Margin = new Thickness(20)
        };
        var header = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(SwitcherTheme.Heading("HomeDeskManager settings", 22));
        header.Children.Add(SwitcherTheme.Caption(
            "Appearance applies immediately. HomeDeskSpace settings are saved to disk; restart HomeDeskSpace to apply them."));
        root.Children.Add(SwitcherTheme.Card(header, new Thickness(14)));

        var settings = new StackPanel { Spacing = 14, Margin = new Thickness(0, 14) };
        settings.Children.Add(SwitcherTheme.Heading("Appearance", 16));
        settings.Children.Add(SettingRow("Theme", theme));
        settings.Children.Add(
            new Border { Height = 1, Background = SwitcherTheme.Border, Margin = new Thickness(0, 2) });
        settings.Children.Add(SwitcherTheme.Heading("HomeDeskSpace", 16));
        settings.Children.Add(SettingRow("Inner gap (DIPs)", innerGap));
        settings.Children.Add(SettingRow("Outer gap (DIPs)", outerGap));
        settings.Children.Add(defaultShortcuts);
        settings.Children.Add(touchpad);
        settings.Children.Add(
            new Border { Height = 1, Background = SwitcherTheme.Border, Margin = new Thickness(0, 2) });
        settings.Children.Add(SwitcherTheme.Heading("Functionality", 16));
        settings.Children.Add(SwitcherTheme.Caption(
            "Alt+number focuses a monitor workspace. Shift+Alt+number moves the selected window there. Reorder monitors to assign keys; disconnected configured monitors keep their slots."));
        var monitorHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) }
        };
        monitorHeader.Children.Add(SwitcherTheme.Caption("Monitor key order"));
        Grid.SetColumn(identifyMonitorsButton, 1);
        monitorHeader.Children.Add(identifyMonitorsButton);
        settings.Children.Add(monitorHeader);
        settings.Children.Add(monitorMappings);

        var scroll = new ScrollViewer { Content = SwitcherTheme.Card(settings, new Thickness(16)) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) },
            Margin = new Thickness(0, 12, 0, 0)
        };
        status.Foreground = SwitcherTheme.MutedText;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(reloadButton);
        actions.Children.Add(saveButton);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(status);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 2);
        root.Children.Add(SwitcherTheme.Card(footer, new Thickness(12, 10)));
        return root;
    }

    private static Control SettingRow(string label, Control control)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) },
            ColumnSpacing = 16
        };
        var caption = SwitcherTheme.Caption(label);
        caption.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(caption);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private async Task ReloadAsync()
    {
        if (busy) return;
        var activeSession = session;
        if (activeSession is not { IsConnected: true })
        {
            if (document is null)
                status.Text = demoMode
                    ? "Demo mode uses sample data. Connect to HomeDeskSpace to read or save its settings."
                    : "Connect to HomeDeskSpace to read or save its settings.";
            UpdatePresentation();
            return;
        }

        busy = true;
        status.Text = "Reading HomeDeskSpace settings…";
        UpdatePresentation();
        try
        {
            document = await activeSession.ReadConfigurationAsync();
            var settings = GeneralSettingsPresentation.Read(document.Content);
            loading = true;
            configuredMonitorOrder = settings.MonitorOrder;
            monitorOrder = BuildMonitorOrder(configuredMonitorOrder, snapshot);
            innerGap.Text = settings.InnerGap.ToString(CultureInfo.InvariantCulture);
            outerGap.Text = settings.OuterGap.ToString(CultureInfo.InvariantCulture);
            defaultShortcuts.IsChecked = settings.EnableDefaultShortcuts;
            touchpad.IsChecked = settings.TouchpadEnabled;
            dirty = false;
            RenderMonitorMappings();
            status.Text = document.IsValid
                ? "Settings loaded from HomeDeskSpace."
                : $"The current file is invalid and may reject saves: {document.ValidationError}";
        }
        catch (Exception error)
        {
            status.Text = $"Could not load HomeDeskSpace settings: {error.Message}";
        }
        finally
        {
            loading = false;
            busy = false;
            UpdatePresentation();
        }
    }

    private async Task SaveAsync()
    {
        if (busy) return;
        if (document is null || session is not { IsConnected: true } activeSession)
        {
            status.Text = "Settings need a live HomeDeskSpace connection. Your current edits are kept.";
            UpdatePresentation();
            return;
        }

        if (!int.TryParse(innerGap.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inner) ||
            !int.TryParse(outerGap.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var outer))
        {
            status.Text = "Inner and outer gaps must be whole numbers from 0 to 256.";
            return;
        }

        busy = true;
        status.Text = "Validating and saving settings…";
        UpdatePresentation();
        try
        {
            var content = GeneralSettingsPresentation.Update(document.Content, inner, outer,
                defaultShortcuts.IsChecked == true, touchpad.IsChecked == true, monitorOrder);
            document = await activeSession.SaveConfigurationAsync(document.Revision, content);
            configuredMonitorOrder = monitorOrder.ToArray();
            dirty = false;
            status.Text = "Saved to disk. Restart HomeDeskSpace to apply these settings.";
        }
        catch (Exception error)
        {
            status.Text = $"Save failed; your edits were kept. {error.Message}";
        }
        finally
        {
            busy = false;
            UpdatePresentation();
        }
    }

    private void UpdatePresentation()
    {
        var canEditHost = !busy && document is not null && session?.IsConnected == true;
        innerGap.IsEnabled = canEditHost;
        outerGap.IsEnabled = canEditHost;
        defaultShortcuts.IsEnabled = canEditHost;
        touchpad.IsEnabled = canEditHost;
        foreach (var button in mappingButtons) button.IsEnabled = canEditHost;
        identifyMonitorsButton.IsEnabled = snapshot?.Desktop.Monitors.Count > 0;
        theme.IsEnabled = !busy;
        reloadButton.IsEnabled = !busy && session?.IsConnected == true;
        saveButton.IsEnabled = canEditHost;
        if (status.Text?.Length > 1200) status.Text = status.Text[..1200] + "…";
    }

    private void MarkDirty()
    {
        if (!loading) dirty = true;
    }

    private static List<string> BuildMonitorOrder(IReadOnlyList<string> configuredOrder,
        ManagerSnapshot? currentSnapshot)
    {
        var result = configuredOrder.ToList();
        if (currentSnapshot is not null)
        {
            var connected = currentSnapshot.Desktop.Monitors
                .OrderBy(monitor => monitor.Bounds.X == 0 && monitor.Bounds.Y == 0 ? 0 : 1)
                .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(monitor => monitor.Id)
                .Select(monitor => monitor.DeviceName);
            foreach (var deviceName in connected)
                if (!result.Contains(deviceName, StringComparer.OrdinalIgnoreCase) && result.Count < 10)
                    result.Add(deviceName);
        }

        return result.Take(10).ToList();
    }

    private void RenderMonitorMappings()
    {
        monitorMappings.Children.Clear();
        mappingButtons.Clear();
        if (monitorOrder.Count == 0)
            monitorMappings.Children.Add(SwitcherTheme.Caption("Connect to HomeDeskSpace to discover monitors."));

        for (var index = 0; index < monitorOrder.Count; index++)
        {
            var rowIndex = index;
            var name = monitorOrder[index];
            var monitor = snapshot?.Desktop.Monitors.FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceName, name, StringComparison.OrdinalIgnoreCase));
            var description = monitor is null
                ? $"{name} · DISCONNECTED · key slot retained"
                : $"{monitor.DeviceName} · {monitor.Bounds.Width}×{monitor.Bounds.Height} at ({monitor.Bounds.X}, {monitor.Bounds.Y})\nID {monitor.Id:D}";
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions
                {
                    new(GridLength.Auto), new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto),
                    new(GridLength.Auto)
                },
                ColumnSpacing = 8
            };
            var key = SwitcherTheme.Heading((index + 1).ToString(CultureInfo.InvariantCulture), 15);
            key.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(SwitcherTheme.Card(key, new Thickness(9, 6)));
            var identity = SwitcherTheme.Caption(description);
            identity.TextWrapping = TextWrapping.Wrap;
            identity.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(identity, 1);
            row.Children.Add(identity);
            var moveUp = SwitcherTheme.Action("↑");
            moveUp.IsEnabled = index > 0;
            moveUp.Click += (_, _) => MoveMonitor(rowIndex, -1);
            Grid.SetColumn(moveUp, 2);
            row.Children.Add(moveUp);
            var moveDown = SwitcherTheme.Action("↓");
            moveDown.IsEnabled = index + 1 < monitorOrder.Count;
            moveDown.Click += (_, _) => MoveMonitor(rowIndex, 1);
            Grid.SetColumn(moveDown, 3);
            row.Children.Add(moveDown);
            mappingButtons.Add(moveUp);
            mappingButtons.Add(moveDown);
            monitorMappings.Children.Add(SwitcherTheme.Card(row, new Thickness(8, 6)));
        }

        UpdatePresentation();
    }

    private void MoveMonitor(int index, int offset)
    {
        var target = index + offset;
        if (target < 0 || target >= monitorOrder.Count) return;
        (monitorOrder[index], monitorOrder[target]) = (monitorOrder[target], monitorOrder[index]);
        dirty = true;
        RenderMonitorMappings();
    }

    private async Task IdentifyMonitorsAsync()
    {
        if (snapshot?.Desktop.Monitors is not { Count: > 0 } monitors) return;
        CloseMonitorIdentifications();
        var lifetime = new CancellationTokenSource();
        identificationLifetime = lifetime;
        identifyMonitorsButton.IsEnabled = false;
        for (var index = 0; index < monitorOrder.Count; index++)
        {
            var name = monitorOrder[index];
            var monitor = monitors.FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceName, name, StringComparison.OrdinalIgnoreCase));
            if (monitor is null) continue;
            const double width = 400;
            const double height = 150;
            var scale = Math.Max(1, monitor.Dpi) / 96d;
            var window = new Window
            {
                Title = $"Monitor {index + 1} · {monitor.DeviceName}",
                Width = width,
                Height = height,
                CanResize = false,
                ShowInTaskbar = false,
                Topmost = true,
                WindowDecorations = WindowDecorations.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(
                    monitor.Bounds.X + (int)(monitor.Bounds.Width / 2d - width * scale / 2),
                    monitor.Bounds.Y + (int)(monitor.Bounds.Height / 2d - height * scale / 2)),
                Content = SwitcherTheme.Card(new StackPanel
                {
                    Spacing = 7,
                    Children =
                    {
                        SwitcherTheme.Heading($"KEY {index + 1} · {monitor.DeviceName}", 23),
                        SwitcherTheme.Caption($"{monitor.Bounds.Width}×{monitor.Bounds.Height} · ID {monitor.Id:D}")
                    }
                }, new Thickness(22))
            };
            monitorIdentificationWindows.Add(window);
            window.Show();
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var window in monitorIdentificationWindows.ToArray())
                if (window.IsVisible)
                    window.Close();
            monitorIdentificationWindows.Clear();
            lifetime.Dispose();
            if (ReferenceEquals(identificationLifetime, lifetime))
            {
                identificationLifetime = null;
                UpdatePresentation();
            }
        }
    }

    private void CloseMonitorIdentifications()
    {
        identificationLifetime?.Cancel();
        foreach (var window in monitorIdentificationWindows.ToArray())
            if (window.IsVisible)
                window.Close();
        monitorIdentificationWindows.Clear();
    }
}