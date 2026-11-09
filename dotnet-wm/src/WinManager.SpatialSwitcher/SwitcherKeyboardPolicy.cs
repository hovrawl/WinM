namespace WinManager.SpatialSwitcher;

public enum SwitcherKeyboardAction
{
    CycleWindowForward,
    CycleWindowBackward,
    CycleWorkspaceForward,
    CycleWorkspaceBackward
}

public readonly record struct SwitcherKeyboardDecision(bool Suppress, SwitcherKeyboardAction? Action = null);

public sealed class SwitcherKeyboardPolicy
{
    private const int TabKey = 0x09;
    private const int SpaceKey = 0x20;
    private bool tabSuppressed;
    private bool spaceSuppressed;

    public SwitcherKeyboardDecision Process(int hookCode, int virtualKey, bool keyDown, bool controlDown,
        bool windowsDown, bool shiftDown, bool altDown, bool injected, bool enabled)
    {
        if (hookCode < 0) return default;
        if (virtualKey is not (TabKey or SpaceKey)) return default;

        var isTab = virtualKey == TabKey;
        var suppressed = isTab ? tabSuppressed : spaceSuppressed;
        if (!keyDown)
        {
            if (!suppressed) return default;
            if (isTab) tabSuppressed = false;
            else spaceSuppressed = false;
            return new(true);
        }

        if (suppressed)
        {
            var repeatsChord = isTab
                ? enabled && !injected && !controlDown && (altDown != windowsDown)
                : enabled && !injected && !controlDown && windowsDown && !altDown;
            return new(true, repeatsChord ? ActionFor(windowsDown, shiftDown) : null);
        }

        var matchesChord = isTab
            ? enabled && !injected && !controlDown && (altDown != windowsDown)
            : enabled && !injected && !controlDown && windowsDown && !altDown;
        if (!matchesChord) return default;

        if (isTab) tabSuppressed = true;
        else spaceSuppressed = true;
        return new(true, ActionFor(windowsDown, shiftDown));
    }

    private static SwitcherKeyboardAction ActionFor(bool windowsDown, bool shiftDown) => (windowsDown, shiftDown) switch
    {
        (false, false) => SwitcherKeyboardAction.CycleWindowForward,
        (false, true) => SwitcherKeyboardAction.CycleWindowBackward,
        (true, false) => SwitcherKeyboardAction.CycleWorkspaceForward,
        _ => SwitcherKeyboardAction.CycleWorkspaceBackward
    };
}