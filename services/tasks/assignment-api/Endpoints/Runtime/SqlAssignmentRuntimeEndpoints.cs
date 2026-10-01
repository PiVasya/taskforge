using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Sql;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain.Sql;
using TaskForge.Tasks.Api.Services.Runtime;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapSqlAssignmentRuntimeEndpoints(WebApplication app)
    {
        app.MapGet("/api/sql-assignments/{assignmentId:guid}", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "sql-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            var root = await db.SqlAssignmentSpecs.AsNoTracking().SingleOrDefaultAsync(x => x.AssignmentId == assignmentId, ct);
            if (root?.PublishedVersionId is null)
                return Results.Conflict(new { code = "SQL_NOT_PUBLISHED", message = "SQL-задание ещё не опубликовано." });
            var version = await db.SqlAssignmentSpecVersions.AsNoTracking().SingleAsync(x => x.Id == root.PublishedVersionId, ct);
            var dataset = await db.SqlDatasetVersions.AsNoTracking().SingleAsync(x => x.Id == version.DatasetVersionId, ct);
            var rows = await (from target in db.SqlAssignmentEngineTargets.AsNoTracking()
                join profile in db.SqlEngineProfiles.AsNoTracking() on target.EngineProfileId equals profile.Id
                where target.SpecVersionId == version.Id && target.Enabled
                orderby target.Sort
                select new
                {
                    engineProfileId = profile.Id,
                    profile.DisplayName,
                    profile.Engine,
                    profile.EngineVersion,
                    profile.Fingerprint,
                    starterSql = target.StarterSqlOverride ?? version.StarterSql
                }).ToListAsync(ct);
            return Results.Ok(new
            {
                assignmentId,
                specVersionId = version.Id,
                datasetVersionId = dataset.Id,
                version.Mode,
                version.AllowMultipleStatements,
                limits = JsonDocument.Parse(version.LimitsJson).RootElement.Clone(),
                targets = rows,
                definition = JsonDocument.Parse(dataset.DefinitionJson).RootElement.Clone(),
                seed = JsonDocument.Parse(dataset.SeedJson).RootElement.Clone()
            });
        });

        app.MapGet("/api/sql-assignments/{assignmentId:guid}/solve-shell", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "sql-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            var solved = await IsSolvedForCurrentUserAsync(loaded.Assignment!, http, cfg, db, clients, ct);
            return Results.Json(await AssignmentTypedReadService.BuildSolveShellAsync(db, loaded.Assignment!, loaded.IncludeSensitive, solved, "/api/sql-assignments", ct), JsonOptions());
        });

        app.MapGet("/api/sql-assignments/{assignmentId:guid}/statement", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "sql-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(await AssignmentTypedReadService.BuildStatementAsync(db, loaded.Assignment!, loaded.IncludeSensitive, ct), JsonOptions());
        });

        app.MapGet("/api/sql-assignments/{assignmentId:guid}/tests", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "sql-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(await AssignmentTypedReadService.BuildTestsAsync(db, loaded.Assignment!, loaded.IncludeSensitive, ct), JsonOptions());
        });


        return app;
    }
}
