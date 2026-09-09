using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Sql;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain.Sql;
using TaskForge.Tasks.Api.Services.Sql;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static void MapSqlEndpoints(WebApplication app)
    {
        var catalog = app.MapGroup("/api/sql").AddEndpointFilter<SqlEndpointFilter>();
        catalog.MapGet("/engines", async (HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            SqlTaskService.Editor(http, cfg);
            var profiles = await db.SqlEngineProfiles.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
            return Results.Ok(profiles.Select(SqlTaskService.Profile));
        });
        catalog.MapGet("/datasets", async (HttpContext http, IConfiguration cfg, TasksDbContext db, string? q, CancellationToken ct) =>
        {
            var (user, admin) = SqlTaskService.Editor(http, cfg);
            var query = db.SqlDatasets.AsNoTracking().Where(x => admin || x.OwnerUserId == user || x.AccessScope == "editor-library");
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Name.Contains(q.Trim()));
            return Results.Ok(await query.OrderBy(x => x.IsArchived).ThenByDescending(x => x.UpdatedAt).Take(200).ToListAsync(ct));
        });
        catalog.MapPost("/datasets", async (SqlDatasetInput input, HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            var (user, _) = SqlTaskService.Editor(http, cfg);
            ValidateDatasetName(input.Name, input.Description, input.AccessScope);
            var dataset = new SqlDataset { Name = input.Name.Trim(), Description = input.Description, OwnerUserId = user, AccessScope = input.AccessScope };
            db.SqlDatasets.Add(dataset); await db.SaveChangesAsync(ct);
            return Results.Ok(dataset);
        });
        catalog.MapGet("/datasets/{id:guid}", async (Guid id, HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            var (user, admin) = SqlTaskService.Editor(http, cfg);
            var dataset = await SqlTaskService.DatasetForEditor(db, id, user, admin, false, ct);
            var versions = await db.SqlDatasetVersions.AsNoTracking().Where(x => x.DatasetId == id).OrderByDescending(x => x.Version)
                .Select(x => new { x.Id, x.Version, x.ContentHash, x.CreatedAt }).ToListAsync(ct);
            return Results.Ok(new { dataset, versions });
        });
        catalog.MapPut("/datasets/{id:guid}", async (Guid id, SqlDatasetUpdate input, HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            var (user, admin) = SqlTaskService.Editor(http, cfg);
            ValidateDatasetName(input.Name, input.Description, input.AccessScope);
            var dataset = await SqlTaskService.DatasetForEditor(db, id, user, admin, false, ct);
            if (input.ConcurrencyStamp != dataset.ConcurrencyStamp) throw new DbUpdateConcurrencyException();
            if (dataset.AccessScope != input.AccessScope && !SqlDatasetAccessPolicy.CanChangeSharing(dataset, user, true, admin)) throw new SqlAccessException();
            if (input.AccessScope == "private" && dataset.OwnerUserId is null) dataset.OwnerUserId = user;
            dataset.Name = input.Name.Trim(); dataset.Description = input.Description;
            dataset.AccessScope = input.AccessScope; dataset.IsArchived = input.IsArchived;
            await db.SaveChangesAsync(ct); return Results.Ok(dataset);
        });
        catalog.MapGet("/datasets/{id:guid}/versions/{versionId:guid}", async (Guid id, Guid versionId, HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            var (user, admin) = SqlTaskService.Editor(http, cfg);
            _ = await SqlTaskService.DatasetForEditor(db, id, user, admin, false, ct);
            var version = await db.SqlDatasetVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId && x.DatasetId == id, ct) ?? throw new SqlNotFoundException();
            return Results.Ok(new { version.Id, version.DatasetId, version.Version, version.ContentHash,
                definition = JsonDocument.Parse(version.DefinitionJson).RootElement.Clone(), seed = JsonDocument.Parse(version.SeedJson).RootElement.Clone(),
                engineOverrides = JsonDocument.Parse(version.EngineOverridesJson).RootElement.Clone() });
        });
        catalog.MapPost("/datasets/{id:guid}/versions", async (Guid id, SqlDatasetVersionInput input, HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            var (user, admin) = SqlTaskService.Editor(http, cfg);
            SqlPortableValidator.Definition(input);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var dataset = await SqlTaskService.DatasetForEditor(db, id, user, admin, true, ct);
            if (input.ConcurrencyStamp != dataset.ConcurrencyStamp) throw new DbUpdateConcurrencyException();
            var version = new SqlDatasetVersion
            {
                DatasetId = id, Version = (await db.SqlDatasetVersions.Where(x => x.DatasetId == id).MaxAsync(x => (int?)x.Version, ct) ?? 0) + 1,
                DefinitionJson = SqlWire.Serialize(input.Definition), SeedJson = SqlWire.Serialize(input.Seed),
                EngineOverridesJson = SqlWire.Serialize(input.EngineOverrides ?? new())
            };
            dataset.UpdatedAt = DateTimeOffset.UtcNow;
            db.SqlDatasetVersions.Add(version); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Ok(new { version.Id, version.DatasetId, version.Version, version.ContentHash, dataset.ConcurrencyStamp });
        });

        var specs = app.MapGroup("/api/assignments/{assignmentId:guid}/sql").AddEndpointFilter<SqlEndpointFilter>();
        specs.MapGet("/edit", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            SqlTaskService.Editor(http, cfg);
            if (!await db.Assignments.AnyAsync(x => x.Id == assignmentId && x.Type == SqlTaskTypes.SqlTest, ct)) throw new SqlNotFoundException();
            return Results.Ok(await SqlTaskService.EditorView(db, assignmentId, ct));
        });
        specs.MapPut("/edit", async (Guid assignmentId, SqlSpecInput input, HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            var (user, admin) = SqlTaskService.Editor(http, cfg);
            await SqlTaskService.SaveSpec(db, assignmentId, input, user, admin, ct);
            return Results.Ok(await SqlTaskService.EditorView(db, assignmentId, ct));
        });
        specs.MapPost("/validate", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            SqlTaskService.Editor(http, cfg);
            var root = await db.SqlAssignmentSpecs.AsNoTracking().FirstOrDefaultAsync(x => x.AssignmentId == assignmentId, ct) ?? throw new SqlNotFoundException();
            var targetIds = db.SqlAssignmentEngineTargets.Where(x => x.SpecVersionId == root.DraftVersionId && x.Enabled).Select(x => x.Id);
            var failed = await db.SqlExpectedArtifacts.Where(x => targetIds.Contains(x.EngineTargetId) && x.Status == "invalid").ToListAsync(ct);
            foreach (var receipt in failed)
            {
                if (receipt.ErrorCode == "SQL_REFERENCE_REQUIRED") continue;
                receipt.Status = "pending"; receipt.ValidationRunId = Guid.NewGuid(); receipt.ExecutionJobId = null;
                receipt.ErrorCode = null; receipt.DiagnosticJson = null; receipt.ValidatedAt = null;
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(await SqlTaskService.EditorView(db, assignmentId, ct));
        });
        specs.MapPost("/publish", async (Guid assignmentId, SqlPublishInput input, HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            SqlTaskService.Editor(http, cfg);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var root = await db.SqlAssignmentSpecs.SingleOrDefaultAsync(x => x.AssignmentId == assignmentId, ct) ?? throw new SqlNotFoundException();
            if (root.ConcurrencyStamp != input.ConcurrencyStamp || root.DraftVersionId != input.VersionId) throw new DbUpdateConcurrencyException();
            var targets = await db.SqlAssignmentEngineTargets.AsNoTracking().Where(x => x.SpecVersionId == input.VersionId && x.Enabled).ToListAsync(ct);
            if (targets.Count == 0) throw new SqlNotReadyException();
            foreach (var target in targets) _ = await SqlTaskService.Payload(db, input.VersionId, target.EngineProfileId, true, ct);
            root.PublishedVersionId = input.VersionId;
            // Visibility remains an independent editor choice. A separate visibility toggle can now expose this revision.
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Ok(await SqlTaskService.EditorView(db, assignmentId, ct));
        });
        specs.MapGet("", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var assignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assignmentId && x.Type == SqlTaskTypes.SqlTest, ct) ?? throw new SqlNotFoundException();
            if (!await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) throw new SqlNotFoundException();
            var root = await db.SqlAssignmentSpecs.AsNoTracking().SingleOrDefaultAsync(x => x.AssignmentId == assignmentId, ct);
            if (root?.PublishedVersionId is null) throw new SqlNotReadyException();
            var version = await db.SqlAssignmentSpecVersions.AsNoTracking().SingleAsync(x => x.Id == root.PublishedVersionId, ct);
            var dataset = await db.SqlDatasetVersions.AsNoTracking().SingleAsync(x => x.Id == version.DatasetVersionId, ct);
            var rows = await (from target in db.SqlAssignmentEngineTargets.AsNoTracking()
                join profile in db.SqlEngineProfiles.AsNoTracking() on target.EngineProfileId equals profile.Id
                where target.SpecVersionId == version.Id && target.Enabled orderby target.Sort
                select new { engineProfileId = profile.Id, profile.DisplayName, profile.Engine, profile.EngineVersion, profile.Fingerprint,
                    starterSql = target.StarterSqlOverride ?? version.StarterSql }).ToListAsync(ct);
            return Results.Ok(new { assignmentId, specVersionId = version.Id, datasetVersionId = dataset.Id, version.Mode, version.AllowMultipleStatements,
                limits = JsonDocument.Parse(version.LimitsJson).RootElement.Clone(), targets = rows,
                definition = JsonDocument.Parse(dataset.DefinitionJson).RootElement.Clone(), seed = JsonDocument.Parse(dataset.SeedJson).RootElement.Clone() });
        });

        var internals = app.MapGroup("/api/internal/sql").AddEndpointFilter<SqlEndpointFilter>();
        internals.MapPost("/engines/register", async (SqlProfileRegistration input, TasksDbContext db, CancellationToken ct) =>
        {
            if (input.Engine is not ("postgresql" or "mysql" or "sqlite") || input.AdapterVersion != SqlWire.AdapterVersion
                || string.IsNullOrWhiteSpace(input.EngineVersion) || input.EngineVersion.Length > 80 || input.RuntimeDigest is null || !input.RuntimeDigest.StartsWith("sha256:")
                || !SqlWire.IsHash(input.RuntimeDigest[7..]) || input.Settings.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Invalid SQL runtime profile.");
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(704638913)", ct);
            var profile = new SqlEngineProfile
            {
                Key = input.Engine, Engine = input.Engine, EngineVersion = input.EngineVersion, RuntimeDigest = input.RuntimeDigest,
                AdapterVersion = input.AdapterVersion, SettingsJson = input.Settings.GetRawText(),
                DisplayName = $"{(input.Engine == "postgresql" ? "PostgreSQL" : input.Engine == "mysql" ? "MySQL" : "SQLite")} {input.EngineVersion}",
                Revision = (await db.SqlEngineProfiles.Where(x => x.Key == input.Engine).MaxAsync(x => (int?)x.Revision, ct) ?? 0) + 1
            };
            profile.Fingerprint = SqlContentKeys.Profile(profile);
            var existing = await db.SqlEngineProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Fingerprint == profile.Fingerprint, ct);
            if (existing is not null) return Results.Ok(SqlTaskService.Profile(existing));
            db.SqlEngineProfiles.Add(profile); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Ok(SqlTaskService.Profile(profile));
        });
        internals.MapGet("/warmup", async (string target, TasksDbContext db, CancellationToken ct) =>
        {
            if (!SqlWire.IsHash(target)) throw new ArgumentException("Exact runtime target is required.");
            var targets = await (from root in db.SqlAssignmentSpecs.AsNoTracking()
                join version in db.SqlAssignmentSpecVersions.AsNoTracking() on root.PublishedVersionId equals (Guid?)version.Id
                join engineTarget in db.SqlAssignmentEngineTargets.AsNoTracking() on version.Id equals engineTarget.SpecVersionId
                join profile in db.SqlEngineProfiles.AsNoTracking() on engineTarget.EngineProfileId equals profile.Id
                where profile.Fingerprint == target && engineTarget.Enabled
                orderby root.UpdatedAt descending
                select new { version.Id, engineTarget.EngineProfileId }).Take(2).ToListAsync(ct);
            var manifests = new List<SqlJobPayload>();
            foreach (var item in targets)
            {
                var manifest = await SqlTaskService.Payload(db, item.Id, item.EngineProfileId, true, ct);
                manifest.ReferenceSql = null; manifest.Expected = null; manifest.ExpectedContentHash = null;
                manifests.Add(manifest);
            }
            return Results.Ok(manifests);
        });
        internals.MapGet("/specs/{specId:guid}/targets/{engineId:guid}", async (Guid specId, Guid engineId, TasksDbContext db, CancellationToken ct) =>
            Results.Ok(await SqlTaskService.Payload(db, specId, engineId, true, ct)));
    }

    private static void ValidateDatasetName(string name, string? description, string scope)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || (description?.Length ?? 0) > 8000 || scope is not ("private" or "editor-library"))
            throw new ArgumentException("Invalid dataset name, description or sharing scope.");
    }
}
