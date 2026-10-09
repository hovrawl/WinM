namespace WinManager.Contracts;

public sealed record ShortcutBinding(uint Modifiers, uint VirtualKey, ManagerCommand Command);