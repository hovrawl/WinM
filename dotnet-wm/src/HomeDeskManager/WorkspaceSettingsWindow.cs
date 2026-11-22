using Avalonia;
using Avalonia.Controls;
using HomeDesk.Contracts;

namespace HomeDeskManager;

internal sealed class WorkspaceSettingsWindow : Window
{
    private readonly WorkspaceDetailsControl workspaceDetails;
    private WorkspaceInfo? workspace;

    public WorkspaceSettingsWindow(Func<ManagerCommand, Task<CommandResult>> executeCommand)
    {
        workspaceDetails = new WorkspaceDetailsControl(executeCommand);
        Title = "HomeDeskManager — Workflow settings";
        Width = 680;
        Height = 360;
        MinWidth = 560;
        MinHeight = 300;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = SwitcherTheme.Canvas;
        Foreground = SwitcherTheme.Text;

        var content = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        content.Children.Add(SwitcherTheme.Heading("Workflow settings", 21));
        content.Children.Add(SwitcherTheme.Caption(
            "Configure the selected workspace layout and layered window spacing."));
        content.Children.Add(workspaceDetails);
        Content = content;
    }

    public void ShowForWorkspace(WorkspaceInfo? selectedWorkspace, bool canEdit)
    {
        workspace = selectedWorkspace;
        workspaceDetails.UpdateWorkspace(workspace, canEdit);
        if (!IsVisible) Show();
        Activate();
    }

    public void UpdateSnapshot(ManagerSnapshot? snapshot, bool canEdit)
    {
        workspace = workspace is { } current
            ? snapshot?.Workspaces?.FirstOrDefault(candidate => candidate.Id == current.Id)
            : null;
        workspaceDetails.UpdateWorkspace(workspace, canEdit);
    }

    public void SetAvailable(bool canEdit) => workspaceDetails.SetAvailable(canEdit);
}