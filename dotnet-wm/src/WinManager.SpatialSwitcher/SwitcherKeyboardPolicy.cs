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
    private bool tabSuppressed;

    public SwitcherKeyboardDecision Process(int hookCode, int virtualKey, bool keyDown, bool controlDown,
        bool windowsDown, bool shiftDown, bool altDown, bool injected, bool enabled)
    {
        if (hookCode < 0) return default;
        if (virtualKey != TabKey) return default;

        if (!keyDown)
        {
            if (!tabSuppressed) return default;
            tabSuppressed = false;
            return new(true);
        }

        if (tabSuppressed)
        {
            if (!enabled || injected || controlDown || altDown == windowsDown) return new(true);
            return new(true, ActionFor(windowsDown, shiftDown));
        }

        if (!enabled || injected || controlDown || altDown == windowsDown) return default;
        tabSuppressed = true;
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