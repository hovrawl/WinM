using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class WindowSemanticsTests
{
    [Theory]
    [InlineData("popup")]
    [InlineData("tool")]
    [InlineData("owned")]
    [InlineData("noactivate")]
    [InlineData("fullscreen")]
    public async Task IgnoredWindowsRemainObservableAndUntouchedUntilExplicitlyTiled(string kind)
    {
        await using var fake = new FakeWindowPlatform();
        var normal = fake.Desktop.Windows[0] with { ClassName = "TeamsWebView" };
        var overlay = normal with
        {
            Id = Guid.NewGuid(), Title = "Sharing control bar | Microsoft Teams",
            Bounds = new(-1182, -231, 446, 53), IsPopup = kind == "popup",
            IsToolWindow = kind == "tool", IsOwned = kind == "owned",
            IsNoActivate = kind == "noactivate", Fullscreen = kind == "fullscreen"
        };
        fake.Desktop = fake.Desktop with { Windows = [normal, overlay], FocusedWindowId = overlay.Id };
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("1"), new("2")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            Assert.Equal(WindowMode.Floating, Find(engine, overlay.Id).Mode);
            Assert.True(Find(engine, overlay.Id).IsIgnored);
            Assert.Equal(overlay.Bounds, Find(engine, overlay.Id).Bounds);
            Assert.DoesNotContain(fake.Plans.SelectMany(plan => plan.Placements), p => p.WindowId == overlay.Id);
            var other = engine.Current!.Workspaces!.Single(w => w.Name == "2");
            Assert.True((await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceId: other.Id), timeout.Token)).Success);
            Assert.True(Find(engine, overlay.Id).Visible);
            Assert.DoesNotContain(fake.Plans.SelectMany(plan => plan.Visibility ?? []), v => v.WindowId == overlay.Id);
            Assert.True((await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceName: "1"), timeout.Token)).Success);

            Assert.True((await engine.ExecuteAsync(new(CommandKind.ToggleMode), timeout.Token)).Success);
            Assert.Equal(WindowMode.Tiling, Find(engine, overlay.Id).Mode);
            Assert.True(Find(engine, overlay.Id).TilingOverride);
            Assert.False(Find(engine, overlay.Id).IsIgnored);
            Assert.Contains(fake.Plans.SelectMany(plan => plan.Placements), p => p.WindowId == overlay.Id);
            Assert.NotEqual(overlay.Bounds, Find(engine, overlay.Id).Bounds);
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            Assert.Equal(WindowMode.Tiling, Find(engine, overlay.Id).Mode);

            Assert.True((await engine.ExecuteAsync(new(CommandKind.ToggleMode, overlay.Id), timeout.Token)).Success);
            Assert.True(Find(engine, overlay.Id).IsIgnored);
            fake.Plans.Clear();
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            Assert.DoesNotContain(fake.Plans.SelectMany(plan => plan.Placements), p => p.WindowId == overlay.Id);
        }
        finally
        {
            timeout.Cancel();
            try { await run; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task ClassificationChangesReflowAutomaticallyAndManualModesSurviveRescans()
    {
        await using var fake = new FakeWindowPlatform();
        var id = fake.Desktop.Windows[0].Id;
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Select(w => w.Id == id ? w with { IsPopup = true } : w).ToArray() };
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            Assert.True(Find(engine, id).IsIgnored);
            Assert.Equal(WindowMode.Floating, Find(engine, id).Mode);
            Assert.True((await engine.ExecuteAsync(new(CommandKind.SetMode, id, Mode: WindowMode.Tiling), timeout.Token)).Success);
            Assert.False(Find(engine, id).IsIgnored);
            Assert.True((await engine.ExecuteAsync(new(CommandKind.SetMode, id, Mode: WindowMode.Floating), timeout.Token)).Success);
            fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Select(w => w.Id == id ? w with { IsPopup = false } : w).ToArray() };
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            Assert.Equal(WindowMode.Floating, Find(engine, id).Mode);
            Assert.False(Find(engine, id).TilingOverride);
            Assert.True((await engine.ExecuteAsync(new(CommandKind.ToggleMode, id), timeout.Token)).Success);
            Assert.Equal(WindowMode.Tiling, Find(engine, id).Mode);
        }
        finally
        {
            timeout.Cancel();
            try { await run; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }

    [Fact]
    public void AHiddenWindowBecomingAnOverlayIsRestoredButAppHiddenWindowsAreNot()
    {
        var monitor = new MonitorInfo(Guid.NewGuid(), "test", new(0, 0, 1000, 900), new(0, 0, 1000, 850), 96);
        var hidden = new WindowInfo(Guid.NewGuid(), monitor.Id, "hidden", "test", "test", new(0, 0, 200, 200),
            false, false, false, WindowMode.Tiling, ManagerHidden: true, IsPopup: true);
        var appHidden = hidden with { Id = Guid.NewGuid(), ManagerHidden = false };
        var desktop = new DesktopSnapshot([monitor], [hidden, appHidden], null);
        var workspaces = new WorkspaceSet([new("1"), new("2")]);
        workspaces.Reconcile(desktop);
        Assert.True(workspaces.Activate(workspaces.Snapshot.Single(w => w.Name == "2").Id));
        Assert.Equal(new WindowVisibility(hidden.Id, true), Assert.Single(workspaces.Visibility(desktop)));
        Assert.All(workspaces.Trees.Values, tree => Assert.Null(tree.Snapshot));
    }

    private static WindowInfo Find(ManagerEngine engine, Guid id) => engine.Current!.Desktop.Windows.Single(w => w.Id == id);
}