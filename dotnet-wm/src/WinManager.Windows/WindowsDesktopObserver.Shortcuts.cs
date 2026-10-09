using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using WinManager.Contracts;
using WinManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace WinManager.Windows;

public sealed partial class WindowsDesktopObserver
{
    private const uint RegisterShortcutsMessage = 0x8005;
    private const uint ShortcutModifiers = 0x400F;
    private readonly Channel<ShortcutRequest> shortcutRequests = Channel.CreateBounded<ShortcutRequest>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly Channel<ShortcutSignal> shortcutSignals = Channel.CreateBounded<ShortcutSignal>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    private readonly HashSet<int> registeredShortcutIds = [];
    private Dictionary<int, ShortcutBinding> shortcutBindings = [];
    private int nextShortcutId = 1;

    public async Task<IReadOnlyList<NativeActionFailure>> RegisterShortcutsAsync(IReadOnlyList<ShortcutBinding> bindings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            if (thread is null) throw new InvalidOperationException("StartAsync must be called before RegisterShortcutsAsync.");
        }
        var copy = bindings.ToArray();
        var failures = ValidateShortcuts(copy);
        if (failures.Count != 0) return failures;
        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var request = new ShortcutRequest(copy, cancellationToken);
        // Once dispatched, wait for native completion so cancellation cannot race unnoticed registrations.
        await shortcutRequests.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (disposing) request.Completion.TrySetException(new ObjectDisposedException(nameof(WindowsDesktopObserver)));
            else if (!PInvoke.PostMessage(notificationWindow, RegisterShortcutsMessage, default, default))
                request.Completion.TrySetException(NativeError("PostMessage"));
        }
        return await request.Completion.Task.ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ManagerCommand> ShortcutCommands([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var signal in shortcutSignals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ReferenceEquals(signal.Bindings, Volatile.Read(ref shortcutBindings)) &&
                signal.Bindings.TryGetValue(signal.Id, out var binding)) yield return binding.Command;
        }
    }

    private static List<NativeActionFailure> ValidateShortcuts(ShortcutBinding[] bindings)
    {
        List<NativeActionFailure> failures = [];
        if (bindings.Length > 256)
        {
            failures.Add(new(null, "invalid_shortcut", "At most 256 shortcuts can be registered."));
            return failures;
        }
        HashSet<(uint Modifiers, uint Key)> combinations = [];
        for (var index = 0; index < bindings.Length; index++)
        {
            var binding = bindings[index];
            if (binding is null || binding.Command is null || (binding.Modifiers & ~ShortcutModifiers) != 0 ||
                binding.VirtualKey is 0 or > 0xFE || binding.VirtualKey == 0x7B)
                failures.Add(new(null, "invalid_shortcut", $"Shortcut {index} has invalid modifiers, command, or virtual key (F12 is reserved)."));
            else if (!combinations.Add((binding.Modifiers & 0xF, binding.VirtualKey)))
                failures.Add(new(null, "duplicate_shortcut", $"Shortcut {index} repeats a key combination."));
        }
        return failures;
    }

    private void ProcessShortcutRequests(HWND window)
    {
        if (shortcutRequests.Reader.TryRead(out var request) && !request.Completion.Task.IsCompleted)
        {
            try
            {
                lock (gate) ObjectDisposedException.ThrowIf(disposing, this);
                request.CancellationToken.ThrowIfCancellationRequested();
                request.Completion.TrySetResult(RegisterShortcuts(window, request.Bindings, request.CancellationToken));
            }
            catch (OperationCanceledException) { request.Completion.TrySetCanceled(request.CancellationToken); }
            catch (Exception error) { request.Completion.TrySetException(error); }
        }
        if (shortcutRequests.Reader.TryPeek(out _)) PInvoke.PostMessage(window, RegisterShortcutsMessage, default, default);
    }

    private IReadOnlyList<NativeActionFailure> RegisterShortcuts(HWND window, ShortcutBinding[] bindings, CancellationToken token)
    {
        List<NativeActionFailure> failures = [];
        if (nextShortcutId + bindings.Length > 0xC000)
            return [new(null, "shortcut_capacity", "Native shortcut IDs are exhausted; create a new observer.")];
        ReleaseShortcuts(window, failures);
        if (failures.Count != 0) return failures;
        Dictionary<int, ShortcutBinding> replacement = [];
        try
        {
            foreach (var binding in bindings)
            {
                token.ThrowIfCancellationRequested();
                var id = nextShortcutId++;
                if (!PInvoke.RegisterHotKey(window, id, (HOT_KEY_MODIFIERS)binding.Modifiers, binding.VirtualKey))
                {
                    var error = Marshal.GetLastWin32Error();
                    failures.Add(new(null, error switch { 1409 => "shortcut_conflict", 5 => "access_denied", _ => "native_error" },
                        $"RegisterHotKey for shortcut {replacement.Count} failed ({error})."));
                    ReleaseShortcuts(window, failures);
                    return failures;
                }
                registeredShortcutIds.Add(id);
                replacement.Add(id, binding);
            }
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref shortcutBindings, replacement);
            return failures;
        }
        catch
        {
            ReleaseShortcuts(window, failures);
            throw;
        }
    }

    private void ReleaseShortcuts(HWND window, List<NativeActionFailure> failures)
    {
        Volatile.Write(ref shortcutBindings, new Dictionary<int, ShortcutBinding>());
        foreach (var id in registeredShortcutIds.ToArray())
        {
            if (PInvoke.UnregisterHotKey(window, id)) registeredShortcutIds.Remove(id);
            else failures.Add(new(null, "native_error", $"UnregisterHotKey failed ({Marshal.GetLastWin32Error()})."));
        }
    }

    private void QueueShortcut(WPARAM id, LPARAM keys)
    {
        var bindings = shortcutBindings;
        if (id.Value > int.MaxValue || !bindings.TryGetValue((int)id.Value, out var binding)) return;
        var packed = unchecked((uint)keys.Value);
        if ((packed & 0xFFFF) != (binding.Modifiers & 0xF) || (packed >> 16) != binding.VirtualKey) return;
        shortcutSignals.Writer.TryWrite(new ShortcutSignal((int)id.Value, bindings));
    }

    private void CompleteShortcuts(Exception? failure)
    {
        shortcutRequests.Writer.TryComplete(failure);
        while (shortcutRequests.Reader.TryRead(out var request))
            request.Completion.TrySetException(failure ?? new ObjectDisposedException(nameof(WindowsDesktopObserver)));
        shortcutSignals.Writer.TryComplete(failure);
    }

    internal unsafe nint ShortcutNotificationHandle { get { lock (gate) return (nint)notificationWindow.Value; } }

    private readonly record struct ShortcutSignal(int Id, Dictionary<int, ShortcutBinding> Bindings);
    private sealed record ShortcutRequest(ShortcutBinding[] Bindings, CancellationToken CancellationToken)
    {
        public TaskCompletionSource<IReadOnlyList<NativeActionFailure>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}