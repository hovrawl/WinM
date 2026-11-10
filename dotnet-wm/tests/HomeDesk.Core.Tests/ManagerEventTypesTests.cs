using System.Reflection;
using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class ManagerEventTypesTests
{
    [Theory]
    [InlineData(ManagerEventTypes.Snapshot, "snapshot")]
    [InlineData(ManagerEventTypes.WindowManaged, "window.managed")]
    [InlineData(ManagerEventTypes.WindowUnmanaged, "window.unmanaged")]
    [InlineData(ManagerEventTypes.WindowUpdated, "window.updated")]
    [InlineData(ManagerEventTypes.FocusChanged, "focus.changed")]
    [InlineData(ManagerEventTypes.SelectionChanged, "selection.changed")]
    [InlineData(ManagerEventTypes.MonitorAdded, "monitor.added")]
    [InlineData(ManagerEventTypes.MonitorRemoved, "monitor.removed")]
    [InlineData(ManagerEventTypes.MonitorUpdated, "monitor.updated")]
    [InlineData(ManagerEventTypes.WorkspaceActivated, "workspace.activated")]
    [InlineData(ManagerEventTypes.WorkspaceDeactivated, "workspace.deactivated")]
    [InlineData(ManagerEventTypes.WorkspaceUpdated, "workspace.updated")]
    [InlineData(ManagerEventTypes.LayoutChanged, "layout.changed")]
    [InlineData(ManagerEventTypes.PauseChanged, "pause.changed")]
    [InlineData(ManagerEventTypes.ApplicationExiting, "application.exiting")]
    public void ConstantsPreserveWireNames(string eventType, string wireName)
    {
        Assert.Equal(wireName, eventType);
        var payload = Protocol.Payload(new SubscribeRequest([eventType]));
        Assert.Equal(wireName, payload.GetProperty("eventTypes")[0].GetString());
        Assert.Contains(eventType, ManagerEngine.EventTypes);
    }

    [Fact]
    public void ConstantsCoverEverySupportedEventWithoutDuplicates()
    {
        var fields = typeof(ManagerEventTypes).GetFields(BindingFlags.Public | BindingFlags.Static);
        Assert.All(fields, field => Assert.True(field.IsLiteral && field.FieldType == typeof(string)));
        var names = fields.Select(field => (string)field.GetRawConstantValue()!).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.True(ManagerEngine.EventTypes.SetEquals(names));
    }
}