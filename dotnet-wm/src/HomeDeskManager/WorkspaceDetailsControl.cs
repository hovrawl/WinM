using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WinManager.Contracts;

namespace WinManager.SpatialSwitcher;

internal sealed class WorkspaceDetailsControl : UserControl
{
    private readonly Func<ManagerCommand, Task<CommandResult>> executeCommand;
    private readonly TextBlock workspaceName = new();
    private readonly TextBlock layoutName = new();
    private readonly TextBox widthRatio = new();
    private readonly TextBox horizontalOffset = new();
    private readonly TextBlock feedback = new();
    private readonly Button splitButton = new();
    private readonly Button layeredButton = new();
    private readonly Button applyButton = new();
    private WorkspaceInfo? workspace;
    private bool available;
    private bool busy;
    private bool updatingOptions;
    private bool optionsDirty;

    public WorkspaceDetailsControl(Func<ManagerCommand, Task<CommandResult>> executeCommand)
    {
        this.executeCommand = executeCommand;
        Content = BuildContent();
        UpdateWorkspace(null, false);
    }

    public bool HasKeyboardFocus => widthRatio.IsFocused || horizontalOffset.IsFocused || splitButton.IsFocused ||
                                    layeredButton.IsFocused || applyButton.IsFocused;

    public void UpdateWorkspace(WorkspaceInfo? nextWorkspace, bool canEdit)
    {
        var workspaceChanged = workspace?.Id != nextWorkspace?.Id;
        workspace = nextWorkspace;
        available = canEdit;
        if (workspaceChanged)
        {
            optionsDirty = false;
            feedback.Text = "";
        }

        workspaceName.Text = workspace?.Name ?? "No workspace selected";
        layoutName.Text =
            workspace is null ? "" : $"CURRENT LAYOUT  ·  {workspace.Layout.ToString().ToUpperInvariant()}";
        if (!optionsDirty) ShowOptions(workspace?.LayeredLayoutOptions ?? LayeredLayoutOptions.Default);
        UpdateActions();
    }

    public void SetAvailable(bool canEdit)
    {
        available = canEdit;
        UpdateActions();
    }

    internal static ManagerCommand CreateLayoutCommand(Guid workspaceId, WorkspaceLayout layout,
        LayeredLayoutOptions options) => new(CommandKind.SetWorkspaceLayout, WorkspaceId: workspaceId,
        Layout: layout, LayeredLayoutOptions: options);

    internal static ManagerCommand CreateToggleLayoutCommand(WorkspaceInfo workspace, WorkspaceLayout layout) =>
        CreateLayoutCommand(workspace.Id, layout,
            workspace.LayeredLayoutOptions ?? LayeredLayoutOptions.Default);

    internal static bool TryParseLayeredOptions(string? widthPercentage, string? offsetText,
        out LayeredLayoutOptions options, out string error)
    {
        options = LayeredLayoutOptions.Default;
        if (!double.TryParse(widthPercentage, NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage) ||
            !double.IsFinite(percentage) || percentage <= 0 || percentage > 100)
        {
            error = "Width must be a percentage greater than 0 and at most 100.";
            return false;
        }

        if (!int.TryParse(offsetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) ||
            offset is < 0 or > 256)
        {
            error = "Horizontal spacing must be a whole number from 0 to 256 DIPs.";
            return false;
        }

        options = new LayeredLayoutOptions(percentage / 100, offset);
        error = "";
        return true;
    }

    internal static bool ShouldHandlePopupKey(bool workspaceControlFocused, Key key) =>
        !workspaceControlFocused &&
        key is Key.Escape or Key.Enter or Key.Space or Key.Left or Key.Right or Key.Up or Key.Down;

    private Control BuildContent()
    {
        var content = new StackPanel { Spacing = 6 };
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) }
        };
        workspaceName.FontSize = 14;
        workspaceName.FontWeight = FontWeight.SemiBold;
        workspaceName.Foreground = SwitcherTheme.Text;
        workspaceName.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(workspaceName);
        layoutName.Foreground = SwitcherTheme.MutedText;
        layoutName.FontSize = 9;
        layoutName.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(layoutName, 1);
        header.Children.Add(layoutName);
        content.Children.Add(header);

        var layoutButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        splitButton.Content = "Use Split";
        StyleAction(splitButton);
        splitButton.Click += async (_, _) => await SetLayoutAsync(WorkspaceLayout.Split);
        layoutButtons.Children.Add(splitButton);
        layeredButton.Content = "Use Layered";
        StyleAction(layeredButton);
        layeredButton.Click += async (_, _) => await SetLayoutAsync(WorkspaceLayout.Layered);
        layoutButtons.Children.Add(layeredButton);
        content.Children.Add(layoutButtons);

        var options = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new(new GridLength(1, GridUnitType.Star)), new(new GridLength(1, GridUnitType.Star)),
                new(GridLength.Auto)
            },
            ColumnSpacing = 8
        };
        var widthGroup = new StackPanel { Spacing = 2 };
        widthGroup.Children.Add(SwitcherTheme.Caption("Preferred width cap (%)"));
        widthRatio.Width = 72;
        widthRatio.Padding = new Thickness(6, 4);
        widthRatio.MaxLength = 8;
        widthRatio.TextChanged += OptionsChanged;
        widthGroup.Children.Add(widthRatio);
        options.Children.Add(widthGroup);

        var offsetGroup = new StackPanel { Spacing = 2 };
        offsetGroup.Children.Add(SwitcherTheme.Caption("Horizontal spacing (DIPs)"));
        horizontalOffset.Width = 72;
        horizontalOffset.Padding = new Thickness(6, 4);
        horizontalOffset.MaxLength = 4;
        horizontalOffset.TextChanged += OptionsChanged;
        offsetGroup.Children.Add(horizontalOffset);
        Grid.SetColumn(offsetGroup, 1);
        options.Children.Add(offsetGroup);
        applyButton.Content = "Apply settings";
        StyleAction(applyButton, true);
        applyButton.VerticalAlignment = VerticalAlignment.Bottom;
        applyButton.Click += async (_, _) => await ApplyOptionsAsync();
        Grid.SetColumn(applyButton, 2);
        options.Children.Add(applyButton);
        content.Children.Add(options);

        feedback.Foreground = SwitcherTheme.MutedText;
        feedback.FontSize = 11;
        feedback.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(feedback);
        return SwitcherTheme.Card(content, new Thickness(10, 8));
    }

    private static void StyleAction(Button button, bool primary = false)
    {
        button.Padding = new Thickness(9, 5);
        button.Background = primary ? SwitcherTheme.Accent : SwitcherTheme.Surface;
        button.Foreground = primary ? SwitcherTheme.AccentText : SwitcherTheme.Text;
        button.BorderBrush = SwitcherTheme.Border;
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(6);
        button.FontSize = 11;
    }

    private void OptionsChanged(object? sender, TextChangedEventArgs args)
    {
        if (updatingOptions) return;
        optionsDirty = true;
        feedback.Text = "";
        UpdateActions();
    }

    private void ShowOptions(LayeredLayoutOptions options)
    {
        updatingOptions = true;
        widthRatio.Text = (options.WindowWidthRatio * 100).ToString("0.##", CultureInfo.InvariantCulture);
        horizontalOffset.Text = options.HorizontalOffset.ToString(CultureInfo.InvariantCulture);
        updatingOptions = false;
    }

    private void UpdateActions()
    {
        var enabled = available && workspace is not null && !busy;
        splitButton.IsEnabled = enabled && workspace?.Layout != WorkspaceLayout.Split;
        layeredButton.IsEnabled = enabled && workspace?.Layout != WorkspaceLayout.Layered;
        applyButton.IsEnabled = enabled && optionsDirty;
    }

    private async Task SetLayoutAsync(WorkspaceLayout layout)
    {
        if (!CanSubmit() || workspace is not { } current || current.Layout == layout) return;
        await SubmitAsync(CreateToggleLayoutCommand(current, layout), optionsUpdate: false);
    }

    private async Task ApplyOptionsAsync()
    {
        if (!CanSubmit() || workspace is not { } current) return;
        if (!TryParseLayeredOptions(widthRatio.Text, horizontalOffset.Text, out var options, out var error))
        {
            feedback.Text = error;
            return;
        }

        await SubmitAsync(CreateLayoutCommand(current.Id, current.Layout, options), optionsUpdate: true);
    }

    private bool CanSubmit() => available && !busy && workspace is not null;

    private async Task SubmitAsync(ManagerCommand command, bool optionsUpdate)
    {
        busy = true;
        feedback.Text = command.Layout == WorkspaceLayout.Layered
            ? "Applying layered layout…"
            : "Applying workspace layout…";
        UpdateActions();
        try
        {
            var result = await executeCommand(command);
            if (result.Success)
            {
                var options = command.LayeredLayoutOptions ?? LayeredLayoutOptions.Default;
                if (workspace is { } currentWorkspace && currentWorkspace.Id == command.WorkspaceId)
                {
                    workspace = currentWorkspace with
                    {
                        Layout = command.Layout ?? currentWorkspace.Layout,
                        LayeredLayoutOptions = options
                    };
                    layoutName.Text = $"CURRENT LAYOUT  ·  {workspace.Layout.ToString().ToUpperInvariant()}";
                    if (optionsUpdate)
                    {
                        optionsDirty = false;
                        ShowOptions(options);
                    }
                }

                feedback.Text = $"Workspace updated · revision {result.StateRevision}";
            }
            else
            {
                feedback.Text = $"Update failed · {result.ErrorCode}: {result.Message}";
            }
        }
        catch (Exception error)
        {
            feedback.Text = $"Update failed · {error.Message}";
        }
        finally
        {
            busy = false;
            UpdateActions();
        }
    }
}