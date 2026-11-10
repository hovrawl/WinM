using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WinManager.Contracts;
using WinManager.Ui;

namespace WinManager.SpatialSwitcher;

internal sealed class GeneralSettingsWindow : Window
{
    private readonly bool demoMode;
    private readonly Action<SwitcherThemeMode> themeChanged;
    private readonly ComboBox theme = new() { MinWidth = 160 };
    private readonly TextBox innerGap = new() { Width = 100 };
    private readonly TextBox outerGap = new() { Width = 100 };
    private readonly CheckBox defaultShortcuts = new() { Content = "Enable default shortcuts" };
    private readonly CheckBox touchpad = new() { Content = "Enable touchpad gestures" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button reloadButton;
    private readonly Button saveButton;
    private ManagerSession? session;
    private ConfigurationDocument? document;
    private bool busy;
    private bool loading;
    private bool dirty;

    public GeneralSettingsWindow(Application application, ManagerSession? session, bool demoMode,
        Action<SwitcherThemeMode> themeChanged)
    {
        this.demoMode = demoMode;
        this.themeChanged = themeChanged;
        Title = "HomeDeskManager — Settings";
        Width = 560;
        Height = 580;
        MinWidth = 460;
        MinHeight = 500;
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

        reloadButton = SwitcherTheme.Action("Reload");
        reloadButton.Click += async (_, _) => await ReloadAsync();
        saveButton = SwitcherTheme.Action("Save settings", primary: true);
        saveButton.Click += async (_, _) => await SaveAsync();
        Content = BuildContent();
        UpdateSession(session);
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
            innerGap.Text = settings.InnerGap.ToString(CultureInfo.InvariantCulture);
            outerGap.Text = settings.OuterGap.ToString(CultureInfo.InvariantCulture);
            defaultShortcuts.IsChecked = settings.EnableDefaultShortcuts;
            touchpad.IsChecked = settings.TouchpadEnabled;
            dirty = false;
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
                defaultShortcuts.IsChecked == true, touchpad.IsChecked == true);
            document = await activeSession.SaveConfigurationAsync(document.Revision, content);
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
        theme.IsEnabled = !busy;
        reloadButton.IsEnabled = !busy && session?.IsConnected == true;
        saveButton.IsEnabled = canEditHost;
        if (status.Text?.Length > 1200) status.Text = status.Text[..1200] + "…";
    }

    private void MarkDirty()
    {
        if (!loading) dirty = true;
    }
}