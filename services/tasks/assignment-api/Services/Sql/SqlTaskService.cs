using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using HttpResults = Microsoft.AspNetCore.Http.Results;
using TaskForge.Sql;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain.Sql;

namespace TaskForge.Tasks.Api.Services.Sql;

internal static class SqlTaskService
{
    internal static SqlProfile Profile(SqlEngineProfile x) => new(x.Id, x.Key, x.DisplayName, x.Engine,
        x.EngineVersion, x.RuntimeDigest, x.AdapterVersion, JsonDocument.Parse(x.SettingsJson).RootElement.Clone(), x.Fingerprint);

    internal static (Guid UserId, bool Admin) Editor(HttpContext http, IConfiguration cfg)
    {
        var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
        var id = TaskForgeRequestSecurity.UserId(http, cfg);
        if (!id.HasValue || principal is null || !TaskForgeRequestSecurity.HasAnyRole(principal, "Admin", "Editor", "LearningEditor"))
            throw new SqlAccessException();
        return (id.Value, TaskForgeRequestSecurity.HasAnyRole(principal, "Admin"));
    }

    internal static async Task<SqlDataset> DatasetForEditor(TasksDbContext db, Guid id, Guid userId, bool admin,
        bool reuse, CancellationToken ct)
    {
        var dataset = await db.SqlDatasets.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new SqlNotFoundException();
        if (!(reuse ? SqlDatasetAccessPolicy.CanReuse(dataset, userId, true, admin) : SqlDatasetAccessPolicy.CanManage(dataset, userId, true, admin)))
            throw new SqlNotFoundException();
        return dataset;
    }

    internal static async Task<SqlAssignmentSpecVersion> SaveSpec(TasksDbContext db, Guid assignmentId,
        SqlSpecInput input, Guid userId, bool admin, CancellationToken ct)
    {
        SqlPortableValidator.Spec(input);
        var assignment = await db.Assignments.SingleOrDefaultAsync(x => x.Id == assignmentId, ct) ?? throw new SqlNotFoundException();
        if (assignment.Type != SqlTaskTypes.SqlTest) throw new ArgumentException("Assignment must have type sql-test.");
        var datasetVersion = await db.SqlDatasetVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == input.DatasetVersionId, ct) ?? throw new SqlNotFoundException();
        _ = await DatasetForEditor(db, datasetVersion.DatasetId, userId, admin, true, ct);
        var profileIds = input.Targets.Select(x => x.EngineProfileId).ToArray();
        var profiles = await db.SqlEngineProfiles.AsNoTracking().Where(x => profileIds.Contains(x.Id)).ToListAsync(ct);
        if (profiles.Count != profileIds.Length) throw new ArgumentException("An engine profile no longer exists.");
        if (profiles.Any(x => x.AdapterVersion != SqlWire.AdapterVersion)) throw new ArgumentException("Unsupported engine adapter contract.");

        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(ct) : null;
        var root = await db.SqlAssignmentSpecs.FirstOrDefaultAsync(x => x.AssignmentId == assignmentId, ct);
        if (root is null)
        {
            if (input.ConcurrencyStamp.HasValue) throw new DbUpdateConcurrencyException();
            root = new SqlAssignmentSpec { AssignmentId = assignmentId };
            db.SqlAssignmentSpecs.Add(root);
            assignment.IsVisible = false;
            await db.SaveChangesAsync(ct);
        }
        else if (input.ConcurrencyStamp != root.ConcurrencyStamp) throw new DbUpdateConcurrencyException();

        var version = new SqlAssignmentSpecVersion
        {
            AssignmentId = assignmentId,
            DatasetVersionId = input.DatasetVersionId,
            Version = (await db.SqlAssignmentSpecVersions.Where(x => x.AssignmentId == assignmentId).MaxAsync(x => (int?)x.Version, ct) ?? 0) + 1,
            Mode = input.Mode,
            StarterSql = input.StarterSql,
            ReferenceSql = input.ReferenceSql,
            AllowMultipleStatements = input.AllowMultipleStatements,
            ResultComparisonSettingsJson = SqlWire.Serialize(input.ResultComparisonSettings),
            StateCheckSettingsJson = SqlWire.Serialize(input.StateCheckSettings),
            SchemaCheckSettingsJson = SqlWire.Serialize(input.SchemaCheckSettings),
            LimitsJson = SqlWire.Serialize(input.Limits)
        };
        db.SqlAssignmentSpecVersions.Add(version);
        var targets = input.Targets.Select(x => new SqlAssignmentEngineTarget
        {
            SpecVersionId = version.Id, EngineProfileId = x.EngineProfileId, Enabled = x.Enabled, Sort = x.Sort,
            StarterSqlOverride = x.StarterSqlOverride, ReferenceSqlOverride = x.ReferenceSqlOverride,
            ResultComparisonSettingsOverrideJson = x.ResultComparisonSettingsOverride is null ? null : SqlWire.Serialize(x.ResultComparisonSettingsOverride),
            StateCheckSettingsOverrideJson = x.StateCheckSettingsOverride is null ? null : SqlWire.Serialize(x.StateCheckSettingsOverride),
            SchemaCheckSettingsOverrideJson = x.SchemaCheckSettingsOverride is null ? null : SqlWire.Serialize(x.SchemaCheckSettingsOverride)
        }).ToArray();
        db.SqlAssignmentEngineTargets.AddRange(targets);
        await db.SaveChangesAsync(ct);
        root.DraftVersionId = version.Id;
        foreach (var target in targets.Where(x => x.Enabled))
        {
            var profile = profiles.Single(x => x.Id == target.EngineProfileId);
            if (!await db.SqlDatasetEngineValidations.AnyAsync(x => x.DatasetVersionId == datasetVersion.Id && x.EngineProfileId == profile.Id, ct)
                && !db.SqlDatasetEngineValidations.Local.Any(x => x.DatasetVersionId == datasetVersion.Id && x.EngineProfileId == profile.Id))
                db.SqlDatasetEngineValidations.Add(new SqlDatasetEngineValidation
                {
                    DatasetVersionId = datasetVersion.Id, EngineProfileId = profile.Id,
                    MaterializationKey = SqlContentKeys.Materialization(datasetVersion, profile)
                });
            db.SqlExpectedArtifacts.Add(new SqlExpectedArtifact
            {
                EngineTargetId = target.Id,
                ArtifactKey = SqlContentKeys.Expected(version, target, datasetVersion, profile),
                Status = string.IsNullOrWhiteSpace(target.ReferenceSqlOverride ?? version.ReferenceSql) ? "invalid" : "pending",
                ErrorCode = string.IsNullOrWhiteSpace(target.ReferenceSqlOverride ?? version.ReferenceSql) ? "SQL_REFERENCE_REQUIRED" : null,
                DiagnosticJson = string.IsNullOrWhiteSpace(target.ReferenceSqlOverride ?? version.ReferenceSql)
                    ? SqlWire.Serialize(new { code = "SQL_REFERENCE_REQUIRED", message = "Add reference SQL before validation and publication." }) : null
            });
        }
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return version;
    }

    internal static async Task<SqlJobPayload> Payload(TasksDbContext db, Guid specId, Guid engineId,
        bool requireReady, CancellationToken ct)
    {
        var spec = await db.SqlAssignmentSpecVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == specId, ct) ?? throw new SqlNotFoundException();
        var target = await db.SqlAssignmentEngineTargets.AsNoTracking().FirstOrDefaultAsync(x => x.SpecVersionId == specId && x.EngineProfileId == engineId && x.Enabled, ct) ?? throw new SqlNotFoundException();
        var dataset = await db.SqlDatasetVersions.AsNoTracking().SingleAsync(x => x.Id == spec.DatasetVersionId, ct);
        var profile = await db.SqlEngineProfiles.AsNoTracking().SingleAsync(x => x.Id == engineId, ct);
        var expected = await db.SqlExpectedArtifacts.AsNoTracking().SingleAsync(x => x.EngineTargetId == target.Id, ct);
        var datasetValid = await db.SqlDatasetEngineValidations.AsNoTracking().AnyAsync(x => x.DatasetVersionId == dataset.Id && x.EngineProfileId == engineId && x.Status == "valid", ct);
        if (requireReady && (expected.Status != "valid" || !datasetValid || expected.ExpectedJson is null))
            throw new SqlNotReadyException();
        var definition = JsonSerializer.Deserialize<SqlDefinition>(dataset.DefinitionJson, SqlWire.Json)!;
        var seed = JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, JsonElement>>>>(dataset.SeedJson, SqlWire.Json)!;
        return new SqlJobPayload
        {
            AssignmentId = spec.AssignmentId, SpecVersionId = spec.Id, DatasetVersionId = dataset.Id, EngineTargetId = target.Id,
            ValidationRunId = requireReady ? null : expected.ValidationRunId,
            Profile = Profile(profile), MaterializationKey = SqlContentKeys.Materialization(dataset, profile),
            ArtifactKey = expected.ArtifactKey, DatasetHash = dataset.ContentHash, Definition = definition, Seed = seed,
            EngineOverrides = JsonSerializer.Deserialize<Dictionary<string, SqlEngineMapping>>(dataset.EngineOverridesJson, SqlWire.Json) ?? new(),
            Mode = spec.Mode, ReferenceSql = requireReady ? null : target.ReferenceSqlOverride ?? spec.ReferenceSql,
            AllowMultipleStatements = spec.AllowMultipleStatements,
            Comparison = JsonSerializer.Deserialize<SqlComparison>(target.ResultComparisonSettingsOverrideJson ?? spec.ResultComparisonSettingsJson, SqlWire.Json)!,
            StateCheck = JsonSerializer.Deserialize<SqlStateCheck>(target.StateCheckSettingsOverrideJson ?? spec.StateCheckSettingsJson, SqlWire.Json)!,
            SchemaCheck = JsonSerializer.Deserialize<SqlSchemaCheck>(target.SchemaCheckSettingsOverrideJson ?? spec.SchemaCheckSettingsJson, SqlWire.Json)!,
            Limits = JsonSerializer.Deserialize<SqlLimits>(spec.LimitsJson, SqlWire.Json)!,
            Expected = requireReady ? JsonDocument.Parse(expected.ExpectedJson!).RootElement.Clone() : null,
            ExpectedContentHash = requireReady ? expected.ContentHash : null
        };
    }

    internal static async Task<object> EditorView(TasksDbContext db, Guid assignmentId, CancellationToken ct)
    {
        var root = await db.SqlAssignmentSpecs.AsNoTracking().FirstOrDefaultAsync(x => x.AssignmentId == assignmentId, ct);
        if (root?.DraftVersionId is null) return new { assignmentId, concurrencyStamp = (Guid?)null, spec = (object?)null };
        var spec = await db.SqlAssignmentSpecVersions.AsNoTracking().SingleAsync(x => x.Id == root.DraftVersionId, ct);
        var targets = await db.SqlAssignmentEngineTargets.AsNoTracking().Where(x => x.SpecVersionId == spec.Id).OrderBy(x => x.Sort).ToListAsync(ct);
        var ids = targets.Select(x => x.Id).ToArray();
        var profileIds = targets.Select(x => x.EngineProfileId).ToArray();
        var receipts = await db.SqlExpectedArtifacts.AsNoTracking().Where(x => ids.Contains(x.EngineTargetId)).ToListAsync(ct);
        var datasetReceipts = await db.SqlDatasetEngineValidations.AsNoTracking().Where(x => x.DatasetVersionId == spec.DatasetVersionId).ToListAsync(ct);
        return new
        {
            assignmentId, root.ConcurrencyStamp, root.PublishedVersionId, draftVersionId = spec.Id,
            datasetId = await db.SqlDatasetVersions.Where(x => x.Id == spec.DatasetVersionId).Select(x => x.DatasetId).SingleAsync(ct),
            profiles = (await db.SqlEngineProfiles.AsNoTracking().Where(x => profileIds.Contains(x.Id)).ToListAsync(ct)).Select(Profile).ToArray(),
            spec = new SqlSpecInput(spec.DatasetVersionId, spec.Mode, spec.StarterSql, spec.ReferenceSql,
                spec.AllowMultipleStatements, JsonSerializer.Deserialize<SqlComparison>(spec.ResultComparisonSettingsJson, SqlWire.Json)!,
                JsonSerializer.Deserialize<SqlStateCheck>(spec.StateCheckSettingsJson, SqlWire.Json)!,
                JsonSerializer.Deserialize<SqlSchemaCheck>(spec.SchemaCheckSettingsJson, SqlWire.Json)!,
                JsonSerializer.Deserialize<SqlLimits>(spec.LimitsJson, SqlWire.Json)!,
                targets.Select(x => new SqlTargetInput(x.EngineProfileId, x.Enabled, x.Sort, x.StarterSqlOverride, x.ReferenceSqlOverride,
                    x.ResultComparisonSettingsOverrideJson is null ? null : JsonSerializer.Deserialize<SqlComparison>(x.ResultComparisonSettingsOverrideJson, SqlWire.Json),
                    x.StateCheckSettingsOverrideJson is null ? null : JsonSerializer.Deserialize<SqlStateCheck>(x.StateCheckSettingsOverrideJson, SqlWire.Json),
                    x.SchemaCheckSettingsOverrideJson is null ? null : JsonSerializer.Deserialize<SqlSchemaCheck>(x.SchemaCheckSettingsOverrideJson, SqlWire.Json))).ToArray(), root.ConcurrencyStamp),
            validation = targets.Where(x => x.Enabled).Select(target =>
            {
                var r = receipts.SingleOrDefault(x => x.EngineTargetId == target.Id);
                var d = datasetReceipts.SingleOrDefault(x => x.EngineProfileId == target.EngineProfileId);
                return new { target.EngineProfileId, datasetStatus = d?.Status ?? "pending", status = r?.Status ?? "pending",
                    error = r?.ErrorCode, diagnostic = r?.DiagnosticJson is null ? (JsonElement?)null : JsonDocument.Parse(r.DiagnosticJson).RootElement.Clone() };
            }).ToArray()
        };
    }

    internal static async Task<bool> HasPublishedRevision(TasksDbContext db, Guid assignmentId, CancellationToken ct)
        => await db.SqlAssignmentSpecs.AnyAsync(x => x.AssignmentId == assignmentId && x.PublishedVersionId != null, ct);

    internal static string ContentHash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
internal sealed class SqlAccessException : Exception { }
internal sealed class SqlNotFoundException : Exception { }
internal sealed class SqlNotReadyException : Exception { }

internal sealed class SqlEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (SqlAccessException) { return HttpResults.Json(new { code = "EDITOR_REQUIRED", message = "Editor access is required." }, statusCode: 403); }
        catch (SqlNotFoundException) { return HttpResults.NotFound(new { code = "SQL_RESOURCE_NOT_FOUND", message = "SQL resource is unavailable." }); }
        catch (SqlNotReadyException) { return HttpResults.Conflict(new { code = "SQL_NOT_VALIDATED", message = "Every enabled engine must pass validation before publication or execution." }); }
        catch (DbUpdateConcurrencyException) { return HttpResults.Conflict(new { code = "SQL_EDIT_CONFLICT", message = "The resource changed. Reload before saving." }); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505")
        { return HttpResults.Conflict(new { code = "SQL_EDIT_CONFLICT", message = "The resource was concurrently created or updated. Reload and retry." }); }
        catch (ArgumentException ex) { return HttpResults.BadRequest(new { code = "SQL_DOCUMENT_INVALID", message = ex.Message }); }
        catch (JsonException ex) { return HttpResults.BadRequest(new { code = "SQL_DOCUMENT_INVALID", message = ex.Message }); }
    }
}
