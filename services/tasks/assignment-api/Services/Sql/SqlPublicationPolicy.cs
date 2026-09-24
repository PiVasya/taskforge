namespace TaskForge.Tasks.Api.Services.Sql;

internal sealed record SqlPublicationTargetState(
    bool Enabled,
    string? ExpectedStatus,
    string? DatasetStatus,
    bool HasExpectedContent);

internal static class SqlPublicationPolicy
{
    internal static bool CanAutoPublishInitialRevision(
        Guid? publishedVersionId,
        Guid? draftVersionId,
        Guid candidateVersionId,
        IReadOnlyCollection<SqlPublicationTargetState> targets)
    {
        if (publishedVersionId is not null || draftVersionId != candidateVersionId) return false;
        var enabled = targets.Where(x => x.Enabled).ToArray();
        if (enabled.Length == 0) return false;
        return enabled.All(x =>
            string.Equals(x.ExpectedStatus, "valid", StringComparison.Ordinal)
            && string.Equals(x.DatasetStatus, "valid", StringComparison.Ordinal)
            && x.HasExpectedContent);
    }
}
