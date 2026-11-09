using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WinManager.Contracts;
using WinManager.Ui;

namespace WinManager.DebugUi;

internal sealed class ConfigurationEditorWindow : Window
{
    private readonly ManagerSession session;
    private readonly Func<ManagerSnapshot?> currentSnapshot;
    private readonly TextBox editor = new();
    private readonly TextBlock pathText = new();
    private readonly TextBlock revisionText = new();
    private readonly TextBlock validationText = new();
    private readonly TextBlock statusText = new();
    private readonly Button reloadButton;
    private readonly Button saveButton;
    private readonly Button captureButton;
    private ConfigurationDocument document;
    private bool busy;
    private bool reloadArmed;

    public ConfigurationEditorWindow(ManagerSession session, ConfigurationDocument document,
        Func<ManagerSnapshot?> currentSnapshot)
    {
        this.session = session;
        this.document = document;
        this.currentSnapshot = currentSnapshot;
        Title = "WinManager — Configuration";
        Width = 980;
        Height = 760;
        MinWidth = 720;
        MinHeight = 560;
        Background = DeskTheme.Canvas;
        Foreground = DeskTheme.Text;

        pathText.TextWrapping = TextWrapping.Wrap;
        revisionText.TextWrapping = TextWrapping.Wrap;
        validationText.TextWrapping = TextWrapping.Wrap;
        statusText.TextWrapping = TextWrapping.Wrap;
        editor.AcceptsReturn = true;
        editor.AcceptsTab = true;
        editor.TextWrapping = TextWrapping.NoWrap;
        editor.FontFamily = new FontFamily("Consolas");
        editor.FontSize = 13;
        editor.Text = document.Content;
        reloadButton = DeskTheme.Action("Reload from disk");
        reloadButton.Click += async (_, _) => await ReloadAsync();
        editor.TextChanged += (_, _) =>
        {
            reloadArmed = false;
            reloadButton.Content = "Reload from disk";
        };
        saveButton = DeskTheme.Action("Save configuration", primary: true);
        saveButton.Click += async (_, _) => await SaveAsync();
        captureButton = DeskTheme.Action("Use current workspace settings");
        captureButton.Click += (_, _) => CaptureWorkspaceSettings();
        Content = BuildContent();
        UpdatePresentation();
    }

    private Control BuildContent()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions
                { new(GridLength.Auto), new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) },
            Margin = new Thickness(18)
        };
        var header = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(DeskTheme.Heading("Manager configuration", 21));
        header.Children.Add(DeskTheme.Caption(
            "Edit this host's JSON file directly or here. Saving updates disk only; restart the manager to apply it."));
        pathText.Foreground = DeskTheme.MutedText;
        header.Children.Add(pathText);
        revisionText.Foreground = DeskTheme.MutedText;
        header.Children.Add(revisionText);
        validationText.Foreground = DeskTheme.Warning;
        header.Children.Add(validationText);
        root.Children.Add(DeskTheme.Card(header, new Thickness(14)));

        var editorCard = DeskTheme.Card(editor, new Thickness(8));
        Grid.SetRow(editorCard, 1);
        root.Children.Add(editorCard);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) },
            Margin = new Thickness(0, 12, 0, 0)
        };
        var feedback = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        feedback.Children.Add(statusText);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(captureButton);
        actions.Children.Add(reloadButton);
        actions.Children.Add(saveButton);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(feedback);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 2);
        root.Children.Add(DeskTheme.Card(footer, new Thickness(12, 10)));
        return root;
    }

    private async Task ReloadAsync()
    {
        if (busy) return;
        if (!reloadArmed && !string.Equals(editor.Text, document.Content, StringComparison.Ordinal))
        {
            reloadArmed = true;
            reloadButton.Content = "Confirm reload";
            statusText.Text = "Reload will replace the editor text with the current file. Click Confirm reload to continue.";
            return;
        }

        busy = true;
        statusText.Text = "Reading configuration from disk…";
        UpdatePresentation();
        try
        {
            document = await session.ReadConfigurationAsync();
            editor.Text = document.Content;
            statusText.Text = "Configuration reloaded. Unsaved editor text was replaced by explicit request.";
        }
        catch (Exception error)
        {
            statusText.Text = $"Reload failed: {error.Message}";
        }
        finally
        {
            reloadArmed = false;
            busy = false;
            UpdatePresentation();
        }
    }

    private async Task SaveAsync()
    {
        if (busy) return;
        busy = true;
        statusText.Text = "Validating and saving configuration…";
        UpdatePresentation();
        try
        {
            document = await session.SaveConfigurationAsync(document.Revision, editor.Text ?? "");
            editor.Text = document.Content;
            statusText.Text = "Saved to disk. Restart the manager for these settings to take effect.";
        }
        catch (Exception error)
        {
            statusText.Text = $"Save failed; editor text was kept. {error.Message}";
        }
        finally
        {
            busy = false;
            UpdatePresentation();
        }
    }

    private void CaptureWorkspaceSettings()
    {
        if (busy) return;
        try
        {
            var snapshot = currentSnapshot() ?? throw new InvalidOperationException("No current manager snapshot is available.");
            editor.Text = ConfigurationEditorPresentation.MergeWorkspaceSettings(editor.Text ?? "", snapshot);
            statusText.Text = "Current workspace names, layouts, options, and positions are staged in the editor; save to persist them.";
        }
        catch (Exception error)
        {
            statusText.Text = $"Workspace settings were not merged: {error.Message}";
        }
    }

    private void UpdatePresentation()
    {
        pathText.Text = $"File: {document.Path}";
        revisionText.Text = $"Revision: {document.Revision}";
        validationText.Text = document.IsValid
            ? "File validates against the manager's configuration rules."
            : $"Current file is invalid and can be edited here: {document.ValidationError}";
        editor.IsEnabled = !busy;
        reloadButton.IsEnabled = !busy;
        saveButton.IsEnabled = !busy;
        captureButton.IsEnabled = !busy;
        if (!reloadArmed) reloadButton.Content = "Reload from disk";
        if (statusText.Text?.Length > 1200) statusText.Text = statusText.Text[..1200] + "…";
    }
}
