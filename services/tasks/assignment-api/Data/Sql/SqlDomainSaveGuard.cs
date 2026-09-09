using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Domain.Sql;

namespace TaskForge.Tasks.Api.Data.Sql;

internal static class SqlDomainSaveGuard
{
    internal static void Prepare(TasksDbContext db)
    {
        db.ChangeTracker.DetectChanges();
        var entries = db.ChangeTracker.Entries().ToArray();
        foreach (var entry in entries)
        {
            if (entry.Entity is ISqlImmutableEntity && (entry.State is EntityState.Modified or EntityState.Deleted))
                throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} is append-only; create a new version instead.");
            if (entry.Entity is SqlExpectedArtifact
                && (entry.State is EntityState.Modified or EntityState.Deleted)
                && entry.OriginalValues.GetValue<string>(nameof(SqlExpectedArtifact.Status)) == SqlValidationStatuses.Valid)
                throw new InvalidOperationException("A validated expected artifact is sealed; create a new spec revision instead.");
        }

        var newSpecs = entries.Where(x => x.State == EntityState.Added).Select(x => x.Entity)
            .OfType<SqlAssignmentSpecVersion>().ToDictionary(x => x.Id);
        var newTargets = entries.Where(x => x.State == EntityState.Added).Select(x => x.Entity)
            .OfType<SqlAssignmentEngineTarget>().ToArray();
        foreach (var target in newTargets)
        {
            if (!newSpecs.ContainsKey(target.SpecVersionId))
                throw new InvalidOperationException("Engine targets must be saved in the same batch as their new spec version.");
            if (target.EngineProfileId == Guid.Empty || target.Sort < 0)
                throw new InvalidOperationException("An engine target needs a profile and a non-negative sort order.");
        }

        foreach (var entry in entries.Where(x => x.State == EntityState.Added))
        {
            switch (entry.Entity)
            {
                case SqlDatasetVersion version:
                    if (version.DatasetId == Guid.Empty || version.Version < 1 || version.DefinitionSchemaVersion < 1)
                        throw new InvalidOperationException("Dataset version identity and schema version are required.");
                    version.ContentHash = SqlContentKeys.Dataset(version);
                    break;
                case SqlEngineProfile profile:
                    if (string.IsNullOrWhiteSpace(profile.Key) || string.IsNullOrWhiteSpace(profile.DisplayName)
                        || string.IsNullOrWhiteSpace(profile.Engine) || string.IsNullOrWhiteSpace(profile.EngineVersion)
                        || string.IsNullOrWhiteSpace(profile.AdapterVersion) || profile.Revision < 1 || profile.SettingsSchemaVersion < 1
                        || string.IsNullOrWhiteSpace(profile.RuntimeDigest)
                        || !profile.RuntimeDigest.StartsWith("sha256:", StringComparison.Ordinal)
                        || !SqlContentKeys.IsHash(profile.RuntimeDigest[7..]))
                        throw new InvalidOperationException("An engine profile needs an exact runtime digest, engine and adapter version.");
                    profile.Fingerprint = SqlContentKeys.Profile(profile);
                    break;
                case SqlAssignmentSpecVersion spec:
                    if (spec.AssignmentId == Guid.Empty || spec.DatasetVersionId == Guid.Empty || spec.Version < 1
                        || spec.ContractVersion < 1 || !SqlTaskModes.IsValid(spec.Mode) || string.IsNullOrWhiteSpace(spec.VerifierVersion))
                        throw new InvalidOperationException("A spec revision needs an assignment, dataset version, valid mode and verifier version.");
                    spec.SpecHash = SqlContentKeys.Spec(spec, newTargets.Where(x => x.SpecVersionId == spec.Id));
                    break;
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in entries.Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            switch (entry.Entity)
            {
                case SqlDataset dataset:
                    if (string.IsNullOrWhiteSpace(dataset.Name)
                        || dataset.AccessScope is not (SqlDatasetScopes.Private or SqlDatasetScopes.EditorLibrary)
                        || dataset.OwnerUserId == Guid.Empty
                        || (dataset.AccessScope == SqlDatasetScopes.Private && dataset.OwnerUserId is null))
                        throw new InvalidOperationException("A dataset needs a name, a supported scope and an owner for private access.");
                    break;
                case SqlDatasetEngineValidation validation:
                    SqlContentKeys.RequireHash(validation.MaterializationKey, nameof(validation.MaterializationKey));
                    ValidateReceipt(validation.Status, validation.ValidationRunId, validation.ValidatedAt, validation.ErrorCode);
                    if (validation.DiagnosticJson is not null) SqlContentKeys.ReadObject(validation.DiagnosticJson);
                    break;
                case SqlExpectedArtifact artifact:
                    SqlContentKeys.RequireHash(artifact.ArtifactKey, nameof(artifact.ArtifactKey));
                    ValidateReceipt(artifact.Status, artifact.ValidationRunId, artifact.ValidatedAt, artifact.ErrorCode);
                    if (artifact.FormatVersion < 1 || artifact.ByteLength < 0
                        || (artifact.ExpectedJson is not null && artifact.ObjectKey is not null))
                        throw new InvalidOperationException("Expected artifact format, size or storage is invalid.");
                    if (artifact.ExpectedJson is not null) SqlContentKeys.ReadObject(artifact.ExpectedJson);
                    if (artifact.DiagnosticJson is not null) SqlContentKeys.ReadObject(artifact.DiagnosticJson);
                    if (artifact.ObjectKey is not null && (string.IsNullOrWhiteSpace(artifact.ObjectKey)
                        || artifact.ObjectKey.StartsWith("/", StringComparison.Ordinal) || artifact.ObjectKey.Contains("://", StringComparison.Ordinal)
                        || artifact.ObjectKey.Split('/').Any(x => x is "." or "..")))
                        throw new InvalidOperationException("Expected artifact storage must be a private relative object key.");
                    if (artifact.ContentHash is not null) SqlContentKeys.RequireHash(artifact.ContentHash, nameof(artifact.ContentHash));
                    if (artifact.Status == SqlValidationStatuses.Valid
                        && (artifact.ContentHash is null || artifact.ByteLength is null || (artifact.ExpectedJson is null && artifact.ObjectKey is null)))
                        throw new InvalidOperationException("A valid expected artifact must have content, a content hash and a size.");
                    break;
            }
            if (entry.Entity is ISqlMutableEntity mutable)
            {
                mutable.ConcurrencyStamp = Guid.NewGuid();
                mutable.UpdatedAt = now;
            }
        }
    }

    private static void ValidateReceipt(string status, Guid runId, DateTimeOffset? validatedAt, string? errorCode)
    {
        if (!SqlValidationStatuses.IsValid(status) || runId == Guid.Empty
            || (status == SqlValidationStatuses.Valid && (validatedAt is null || errorCode is not null)))
            throw new InvalidOperationException("Invalid SQL validation receipt state.");
    }
}
