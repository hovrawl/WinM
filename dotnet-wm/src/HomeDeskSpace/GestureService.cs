using System.Threading.Channels;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinManager.Contracts;
using WinManager.Core;
using WinManager.Windows;

namespace WinManager.Host;

internal static class TouchpadGesturePolicy
{
    public static ManagerCommand? CreateCommand(TouchpadGesture gesture, TouchpadCapabilityResult capability)
    {
        if (gesture.Kind != TouchpadGestureKind.Swipe || gesture.Direction is not { } direction) return null;

        if (gesture.FingerCount == 3 && direction is TouchpadSwipeDirection.Left or TouchpadSwipeDirection.Right)
        {
            if (!capability.CanRecognizeGestures) return null;
            // Cycling wraps within the workspace instead of spilling focus onto another monitor.
            return new ManagerCommand(CommandKind.FocusCycle, CycleDirection: direction == TouchpadSwipeDirection.Left
                ? FocusCycleDirection.Next
                : FocusCycleDirection.Previous);
        }

        if (!capability.CanRecognizeGestures || gesture.FingerCount != 4) return null;
        var physicalDirection = direction switch
        {
            TouchpadSwipeDirection.Left => Direction.Left,
            TouchpadSwipeDirection.Right => Direction.Right,
            TouchpadSwipeDirection.Up => Direction.Up,
            TouchpadSwipeDirection.Down => Direction.Down,
            _ => throw new ArgumentOutOfRangeException(nameof(gesture))
        };
        return new ManagerCommand(CommandKind.Move, Direction: physicalDirection);
    }
}

[SupportedOSPlatform("windows10.0.15063")]
internal sealed class GestureService(
    TouchpadInputSource input,
    WindowsDesktopObserver observer,
    ManagerEngine manager,
    ManagerConfiguration configuration,
    ILogger<GestureService> logger) : BackgroundService
{
    private readonly Channel<TouchpadGesture> gestures = Channel.CreateBounded<TouchpadGesture>(
        new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.Touchpad.Enabled)
        {
            logger.LogInformation("Native touchpad gestures are disabled by configuration.");
            return;
        }

        input.GestureRecognized += OnGestureRecognized;
        input.CapabilityChanged += OnCapabilityChanged;
        try
        {
            await manager.Ready.WaitAsync(stoppingToken);
            await input.StartAsync(stoppingToken);
            LogCapability(input.Capability);
            await foreach (var gesture in gestures.Reader.ReadAllAsync(stoppingToken))
            {
                var command = TouchpadGesturePolicy.CreateCommand(gesture, input.Capability);
                if (command is null) continue;

                var foregroundWindow = await observer.ResolveManagedForegroundWindowAsync(stoppingToken);
                var snapshot = manager.Current;
                if (foregroundWindow is not { } windowId || snapshot?.Desktop.FocusedWindowId != windowId)
                    continue;

                var result = await manager.ExecuteAsync(command, stoppingToken);
                if (!result.Success)
                    logger.LogWarning("Native touchpad gesture rejected: {Code} {Message}", result.ErrorCode,
                        result.Message);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(error, "Native touchpad gestures are unavailable; keyboard shortcuts remain active.");
        }
        finally
        {
            input.GestureRecognized -= OnGestureRecognized;
            input.CapabilityChanged -= OnCapabilityChanged;
            gestures.Writer.TryComplete();
        }
    }

    private void OnGestureRecognized(TouchpadGesture gesture)
    {
        if (gesture.Kind == TouchpadGestureKind.Swipe) gestures.Writer.TryWrite(gesture);
    }

    private void OnCapabilityChanged(TouchpadCapabilityResult capability) => LogCapability(capability);

    private void LogCapability(TouchpadCapabilityResult capability)
    {
        if (capability.CanRecognizeGestures && !capability.GestureConflictSuppressionVerified)
        {
            logger.LogWarning(
                "Native touchpad capability: {State}; devices {DeviceCount}; contacts observed {Contacts}; {Detail}. Three-finger horizontal focus and four-finger window movement may also trigger Windows' configured touchpad action because suppression is unverified.",
                capability.State, capability.DeviceCount, capability.ContactReportsObserved, capability.Detail);
            return;
        }

        logger.LogInformation(
            "Native touchpad capability: {State}; devices {DeviceCount}; contacts observed {Contacts}; {Detail}",
            capability.State, capability.DeviceCount, capability.ContactReportsObserved, capability.Detail);
    }
}