using WinManager.Cli;
using WinManager.Contracts;
using Xunit;

namespace WinManager.Ipc.Tests;

public sealed class StatusDisplayTests
{
    [Fact]
    public void FormatsFocusedWindowAndItsWorkspaceFromEachFullSnapshot()
    {
        var monitor = Guid.NewGuid();
        var window = new WindowInfo(Guid.NewGuid(), monitor, "Editor", "editor", "test",
            new(0, 0, 100, 100), true, false, false, WindowMode.Tiling);
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 12, new([], [window], window.Id), new Dictionary<Guid, SplitNodeInfo?>(), false,
            [new(Guid.NewGuid(), "Other", Guid.NewGuid(), true, []), new(Guid.NewGuid(), "Code", monitor, true, [window.Id])]);

        var lines = StatusDisplay.Format(Event(snapshot, ManagerEventTypes.FocusChanged));
        Assert.Equal("Current Workspace: Code", lines[0]);
        Assert.Equal($"Current Window: Editor [editor] ({window.Id})", lines[1]);
        Assert.Equal("Current Config direction: Not exposed by host", lines[2]);
        Assert.Equal("Latest event: focus.changed (sequence 7, revision 12; running)", lines[3]);

        lines = StatusDisplay.Format(Event(snapshot with { Desktop = new([], [], null), Paused = true }, ManagerEventTypes.PauseChanged));
        Assert.Equal("Current Workspace: Other, Code", lines[0]);
        Assert.Equal("Current Window: None", lines[1]);
        Assert.Contains("pause.changed", lines[3]);
        Assert.Contains("paused", lines[3]);
    }

    [Fact]
    public void HandlesMissingWorkspacesAndUnknownFocusedWindow()
    {
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 1, new([], [], Guid.NewGuid()), new Dictionary<Guid, SplitNodeInfo?>(), false);
        var lines = StatusDisplay.Format(Event(snapshot));
        Assert.Equal("Current Workspace: None", lines[0]);
        Assert.Equal("Current Window: None", lines[1]);
    }

    [Fact]
    public void RemovesControlCharactersFromHostText()
    {
        var window = new WindowInfo(Guid.NewGuid(), Guid.NewGuid(), "Bad\r\n\u001b[2J\tTitle", "app\b", "test",
            new(0, 0, 100, 100), true, false, false, WindowMode.Tiling);
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 1, new([], [window], window.Id), new Dictionary<Guid, SplitNodeInfo?>(), false,
            [new(Guid.NewGuid(), "Work\nspace", window.MonitorId, true, [window.Id])]);
        var lines = StatusDisplay.Format(Event(snapshot));
        Assert.All(lines, line => Assert.DoesNotContain(line, char.IsControl));
        Assert.Equal("Current Workspace: Work space", lines[0]);
        Assert.Contains("Bad   [2J Title", lines[1]);
    }

    [Fact]
    public void ReusesFourRowsAndClearsOldLongerValuesEvenAtBufferBottom()
    {
        var terminal = new TestTerminal { CursorTop = 9, Height = 10 };
        var display = new StatusDisplay(terminal);
        display.Update(["Workspace long", "Window long", "Direction long", "Event long"]);
        var top = terminal.CursorTop - 4;
        display.Update(["W", "X", "D", "E"]);
        Assert.Equal(top + 4, terminal.CursorTop);
        Assert.Equal("W".PadRight(terminal.Width - 1), terminal.Rows[top]);
        Assert.Equal("E".PadRight(terminal.Width - 1), terminal.Rows[top + 3]);
        Assert.Equal(10, terminal.Moves);
    }

    [Fact]
    public void ClipsToWidthWithoutWrappingAndAdaptsToResize()
    {
        var terminal = new TestTerminal { Width = 12 };
        var display = new StatusDisplay(terminal);
        display.Update(["abcdefghijklmnopqrstuvwxyz", "b", "c", "d"]);
        Assert.Equal("abcdefghij…", terminal.Rows[0]);
        terminal.Width = 6;
        display.Update(["abcdefghijklmnopqrstuvwxyz", "b", "c", "d"]);
        Assert.Equal("abcd…", terminal.Rows[0]);
        Assert.Equal(4, terminal.CursorTop);
    }

    [Fact]
    public void ReservesColumnsForWideUnicodeAndDoesNotSplitSurrogatePairs()
    {
        var terminal = new TestTerminal { Width = 8 };
        var display = new StatusDisplay(terminal);
        display.Update(["界界界界", "😀😀😀😀", "c", "d"]);
        Assert.Equal("界界界…", terminal.Rows[0].TrimEnd());
        Assert.Equal("😀😀😀…", terminal.Rows[1].TrimEnd());
        display.Update(["a", "b", "c", "d"]);
        Assert.Equal("a".PadRight(7), terminal.Rows[0]);
        Assert.Equal("b".PadRight(7), terminal.Rows[1]);
    }

    [Theory]
    [InlineData(true, 20)]
    [InlineData(false, 4)]
    public void RedirectedOrTinyTerminalsAppendPlainBlocks(bool redirected, int height)
    {
        var terminal = new TestTerminal { IsOutputRedirected = redirected, Height = height };
        var display = new StatusDisplay(terminal);
        display.Update(["a", "b", "c", "d"]);
        display.Update(["e", "f", "g", "h"]);
        Assert.Equal(0, terminal.Moves);
        Assert.Equal(new[] { "a", "b", "c", "d", "e", "f", "g", "h" }, terminal.Writes);
    }

    private static EventEnvelope Event(ManagerSnapshot snapshot, string type = ManagerEventTypes.Snapshot) =>
        new(Protocol.Version, snapshot.InstanceId, 7, snapshot.StateRevision, type, Protocol.Payload(snapshot));

    private sealed class TestTerminal : IStatusTerminal
    {
        public bool IsOutputRedirected { get; init; }
        public int Width { get; set; } = 40;
        public int Height { get; init; } = 20;
        public int CursorTop { get; set; }
        public int Moves { get; private set; }
        public Dictionary<int, string> Rows { get; } = [];
        public List<string> Writes { get; } = [];
        public void SetCursorPosition(int left, int top) { CursorTop = top; Moves++; }
        public void Write(string value) => Rows[CursorTop] = value;
        public void WriteLine(string value)
        {
            var previous = Rows.GetValueOrDefault(CursorTop, "");
            Rows[CursorTop] = value + (previous.Length > value.Length ? previous[value.Length..] : "");
            Writes.Add(value);
            if (++CursorTop < Height || IsOutputRedirected) return;
            CursorTop = Height - 1;
            var rows = Rows.ToArray();
            Rows.Clear();
            foreach (var (row, text) in rows.Where(r => r.Key > 0)) Rows[row - 1] = text;
        }
    }
}