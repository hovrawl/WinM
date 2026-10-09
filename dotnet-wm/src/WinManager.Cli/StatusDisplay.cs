using System.Text;
using System.Text.Json;
using WinManager.Contracts;

namespace WinManager.Cli;

internal sealed class StatusDisplay(IStatusTerminal terminal)
{
    private bool drawn;

    public static string[] Format(EventEnvelope envelope)
    {
        var snapshot = envelope.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!;
        var window = snapshot.Desktop.Windows.FirstOrDefault(w => w.Id == snapshot.Desktop.FocusedWindowId);
        var workspaces = snapshot.Workspaces ?? [];
        var workspace = window is null ? null : workspaces.FirstOrDefault(w => w.WindowIds.Contains(window.Id))
            ?? workspaces.FirstOrDefault(w => w.Active && w.MonitorId == window.MonitorId);
        var workspaceText = workspace is not null ? workspace.Name :
            string.Join(", ", workspaces.Where(w => w.Active).Select(w => w.Name));
        return
        [
            $"Current Workspace: {(workspaceText.Length == 0 ? "None" : Clean(workspaceText))}",
            $"Current Window: {(window is null ? "None" : $"{Clean(window.Title)} [{Clean(window.ProcessName)}] ({window.Id})")}",
            "Current Config direction: Not exposed by host",
            $"Latest event: {Clean(envelope.EventType)} (sequence {envelope.Sequence}, revision {envelope.StateRevision}; {(snapshot.Paused ? "paused" : "running")})"
        ];
    }

    public void Update(IReadOnlyList<string> lines)
    {
        if (terminal.IsOutputRedirected || terminal.Height <= lines.Count || terminal.Width < 2)
        {
            foreach (var line in lines) terminal.WriteLine(line);
            drawn = false;
            return;
        }

        if (!drawn)
            foreach (var _ in lines) terminal.WriteLine("");
        var top = Math.Max(0, terminal.CursorTop - lines.Count);
        var width = terminal.Width - 1;
        terminal.SetCursorPosition(0, top);
        foreach (var line in lines)
        {
            terminal.Write(new string(' ', width));
            terminal.SetCursorPosition(0, terminal.CursorTop);
            terminal.WriteLine(Clip(line, width));
        }
        drawn = true;
    }

    private static string Clean(string value) => new(value.Select(c => char.IsControl(c) ||
        c is '\u2028' or '\u2029' ? ' ' : c).ToArray());

    private static string Clip(string value, int width)
    {
        var columns = 0;
        foreach (var rune in value.EnumerateRunes()) columns += rune.IsAscii ? 1 : 2;
        if (columns <= width) return value;
        var result = new StringBuilder();
        columns = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var size = rune.IsAscii ? 1 : 2;
            if (columns + size > width - 1) break;
            result.Append(rune.ToString());
            columns += size;
        }
        return result.Append('…').ToString();
    }
}

internal interface IStatusTerminal
{
    bool IsOutputRedirected { get; }
    int Width { get; }
    int Height { get; }
    int CursorTop { get; }
    void SetCursorPosition(int left, int top);
    void Write(string value);
    void WriteLine(string value);
}

internal sealed class ConsoleStatusTerminal : IStatusTerminal
{
    public bool IsOutputRedirected => Console.IsOutputRedirected;
    public int Width => Console.WindowWidth;
    public int Height => Console.BufferHeight;
    public int CursorTop => Console.CursorTop;
    public void SetCursorPosition(int left, int top) => Console.SetCursorPosition(left, top);
    public void Write(string value) => Console.Write(value);
    public void WriteLine(string value) => Console.WriteLine(value);
}