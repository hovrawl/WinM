using WinManager.Contracts;

namespace WinManager.DebugUi;

internal static class DemoSnapshot
{
    public static ManagerSnapshot Create()
    {
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var code = Guid.NewGuid();
        var chat = Guid.NewGuid();
        var unviewed = Guid.NewGuid();
        var staged = Guid.NewGuid();
        var overflow = Guid.NewGuid();
        var ignored = Guid.NewGuid();
        var monitors = new MonitorInfo[]
        {
            new(left, "DISPLAY-A", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96),
            new(right, "DISPLAY-B", new(1920, 0, 1920, 1080), new(1920, 0, 1920, 1040), 144)
        };
        WindowInfo[] windows =
        [
            new(staged, left, "Editor — sample.cs", "Editor", "SampleEditor", new(8, 8, 1200, 900), true, false,
                false, WindowMode.Tiling),
            new(overflow, left, "Preview — cannot fit", "Preview", "PreviewWindow", new(0, 0, 1920, 1040), true, true,
                false, WindowMode.Tiling, MinimumWidth: 2400, LayoutOverflow: true),
            new(ignored, right, "Full-screen app (default ignored)", "Video", "VideoSurface", new(1920, 0, 1920, 1080), true, false,
                true, WindowMode.Tiling)
        ];
        WorkspaceInfo[] workspaces =
        [
            new(code, "Code", left, true, [staged, overflow, ignored]),
            new(chat, "Chat", right, true, []),
            new(unviewed, "Unviewed", Guid.Empty, false, [])
        ];
        var exclusion = new ConfiguredWindowExclusion("Excluded by config", "sample-host", "SampleHostWindow",
            right, new(2050, 80, 600, 400), ProcessNameExcluded: true, ClassNameExcluded: false);
        return new ManagerSnapshot(Guid.NewGuid(), 42, new DesktopSnapshot(monitors, windows, staged, [exclusion]),
            new Dictionary<Guid, SplitNodeInfo?>(), false, workspaces, null,
            [new(left, code), new(right, chat)]);
    }
}
