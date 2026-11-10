using System.Text.Json;
using System.Text.Json.Nodes;
using WinManager.Contracts;
using Xunit;

namespace WinManager.Ipc.Tests;

public sealed class DiagnosticContractTests
{
    [Fact]
    public void ConfiguredExclusionsRoundTripSeparatelyFromActionableWindows()
    {
        var exclusion = new ConfiguredWindowExclusion("Excluded", "sample", "SampleClass", Guid.NewGuid(),
            new(10, 20, 300, 400), true, false);
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 5, new([], [], null, [exclusion]),
            new Dictionary<Guid, SplitNodeInfo?>(), false);

        var payload = Protocol.Payload(snapshot);
        var restored = payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!;

        Assert.Equal(exclusion, Assert.Single(restored.Desktop.ConfiguredExclusions!));
        Assert.Empty(restored.Desktop.Windows);
        Assert.False(payload.GetProperty("desktop").GetProperty("configuredExclusions")[0].TryGetProperty("id", out _));
    }

    [Fact]
    public void OlderSnapshotsCanOmitConfiguredExclusions()
    {
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 5, new([], [], null),
            new Dictionary<Guid, SplitNodeInfo?>(), false);
        var payload = JsonNode.Parse(Protocol.Payload(snapshot).GetRawText())!.AsObject();
        Assert.True(payload["desktop"]!.AsObject().Remove("configuredExclusions"));

        var restored = payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!;

        Assert.Null(restored.Desktop.ConfiguredExclusions);
        Assert.Empty(restored.Desktop.Windows);
    }

    [Fact]
    public void PreviewTargetRequestsAreSourceGeneratedAndStrictlyBounded()
    {
        var request = new PreviewTargetsRequest([Guid.NewGuid(), Guid.NewGuid()]);
        var payload = Protocol.Payload(request);
        var restored = payload.Deserialize(Protocol.TypeInfo<PreviewTargetsRequest>())!;

        Assert.True(PreviewTargetsRequestRules.IsValid(restored));
        Assert.False(PreviewTargetsRequestRules.IsValid(new PreviewTargetsRequest([])));
        Assert.False(PreviewTargetsRequestRules.IsValid(new PreviewTargetsRequest([Guid.Empty])));
        var duplicate = Guid.NewGuid();
        Assert.False(PreviewTargetsRequestRules.IsValid(new PreviewTargetsRequest([duplicate, duplicate])));
        Assert.False(PreviewTargetsRequestRules.IsValid(new PreviewTargetsRequest(
            Enumerable.Range(0, PreviewTargetsRequestRules.MaximumWindowIds + 1).Select(_ => Guid.NewGuid())
                .ToArray())));
    }
}