namespace HomeDesk.Contracts;

public sealed record PreviewTargetsRequest(IReadOnlyList<Guid>? WindowIds);

public sealed record WindowPreviewTarget(
    Guid WindowId,
    long HandleValue,
    uint ProcessId,
    ulong ProcessCreationTime);

public sealed record PreviewTargetFailure(Guid WindowId, string Code);

public sealed record PreviewTargetsResult(
    long StateRevision,
    IReadOnlyList<WindowPreviewTarget> Targets,
    IReadOnlyList<PreviewTargetFailure> Failures);

public interface IPreviewTargetResolver
{
    Task<PreviewTargetsResult> ResolvePreviewTargetsAsync(IReadOnlyList<Guid> windowIds, long stateRevision,
        CancellationToken cancellationToken);
}

public static class PreviewTargetsRequestRules
{
    public const int MaximumWindowIds = 64;

    public static bool IsValid(PreviewTargetsRequest? request) =>
        request?.WindowIds is { Count: > 0 and <= MaximumWindowIds } ids &&
        ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count;
}