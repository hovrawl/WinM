using Avalonia.Input;

namespace HomeDeskManager;

internal static class SwitcherAcceptKeyPolicy
{
    public static bool IsAcceptKey(Key key, KeyModifiers modifiers, bool textInputFocused = false) =>
        key == Key.Enter || key == Key.Space && modifiers == KeyModifiers.None && !textInputFocused;
}