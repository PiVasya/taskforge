using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Runtime;
using TaskForge.Tasks.Api.Services.Specs;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapImageAssignmentRuntimeEndpoints(WebApplication app)
    {
        app.MapGet("/api/image-assignments/{assignmentId:guid}", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "image-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            var solved = await IsSolvedForCurrentUserAsync(loaded.Assignment!, http, cfg, db, clients, ct);
            return Results.Json(await AssignmentTypedReadService.BuildDtoAsync(db, loaded.Assignment!, loaded.IncludeSensitive, solved, ct), JsonOptions());
        });

        app.MapGet("/api/image-assignments/{assignmentId:guid}/solve-shell", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "image-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            var solved = await IsSolvedForCurrentUserAsync(loaded.Assignment!, http, cfg, db, clients, ct);
            return Results.Json(await AssignmentTypedReadService.BuildSolveShellAsync(db, loaded.Assignment!, loaded.IncludeSensitive, solved, "/api/image-assignments", ct), JsonOptions());
        });

        app.MapGet("/api/image-assignments/{assignmentId:guid}/statement", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "image-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(await AssignmentTypedReadService.BuildStatementAsync(db, loaded.Assignment!, loaded.IncludeSensitive, ct), JsonOptions());
        });

        app.MapGet("/api/image-assignments/{assignmentId:guid}/tests", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "image-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(await AssignmentTypedReadService.BuildTestsAsync(db, loaded.Assignment!, loaded.IncludeSensitive, ct), JsonOptions());
        });

        app.MapPost("/api/image-assignments/{assignmentId:guid}/reference", async (
            Guid assignmentId, HttpRequest req, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (CheckUserRateLimit(http, cfg, "image-test") is { } limited) return limited;
            if (!IsEditor(http, cfg)) return Results.Json(new { message = "Для загрузки эталона нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
            var assignment = await db.Assignments.FindAsync([assignmentId], ct);
            if (assignment == null) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            if (!await CanUserEditCourseAsync(assignment.CourseId, http, cfg, clients, ct)) return CourseEditForbidden();
            if (!string.Equals(AssignmentTypedReadService.NormalizeStoredType(assignment), "image-test", StringComparison.Ordinal))
                return Results.Conflict(new { message = "Задание не является image-test.", code = "IMAGE_ASSIGNMENT_TYPE_REQUIRED" });

            var form = await req.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file == null || file.Length == 0) return Problem(400, "IMAGE_REFERENCE_REQUIRED", "request.validation", "Выберите эталонную картинку для image-test.");
            if (file.Length > MaxImageUploadBytes(cfg)) return Problem(413, "IMAGE_REFERENCE_TOO_LARGE", "request.validation", $"Эталонная картинка слишком большая. Максимум: {MaxImageUploadBytes(cfg) / 1024 / 1024} МБ.");
            var threshold = form.TryGetValue("threshold", out var t) && int.TryParse(t, out var tv) ? System.Math.Clamp(tv, 0, 100) : 90;
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var uploaded = await UploadImageBytesToFilesApiAsync(clients, cfg, ms.ToArray(), file.FileName, string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, $"image-tests/reference/{assignmentId:N}", ct);
            var imageSpec = await db.ImageAssignmentSpecs.SingleOrDefaultAsync(x => x.AssignmentId == assignmentId, ct)
                ?? throw new InvalidOperationException($"DATA_INTEGRITY_ERROR: assignment {assignmentId:D} (image-test) is missing its typed spec.");
            var payload = JsonNode.Parse(imageSpec.TestsJson ?? "{}") as JsonObject ?? new JsonObject();
            payload["imageTestReferenceKey"] = uploaded.Key;
            payload["imageTestReferenceUrl"] = uploaded.PrivateUrl;
            payload["imageTestSimilarityThreshold"] = threshold;
            payload["referenceFileName"] = uploaded.FileName;
            payload["referenceContentType"] = uploaded.ContentType;
            payload["referenceSize"] = uploaded.Size;
            RemoveInlineImageFields(payload);
            await AssignmentTypeSpecService.SaveImageTestsJsonAsync(db, assignment, payload.ToJsonString(JsonOptions()), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { assignmentId, key = uploaded.Key, url = uploaded.PrivateUrl, privateUrl = uploaded.PrivateUrl, threshold, storage = "minio" });
        }).DisableAntiforgery();

        app.MapPost("/api/image-assignments/{assignmentId:guid}/compare", async (
            Guid assignmentId, HttpRequest req, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients) =>
        {
            if (CheckUserRateLimit(http, cfg, "image-test") is { } limited) return limited;
            return await CompareImageUpload(assignmentId, req, http, cfg, db, clients);
        });

        app.MapPost("/api/image-assignments/{assignmentId:guid}/run-code", async (
            Guid assignmentId, ImageCodeRequest request, HttpContext http, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg) =>
        {
            if (CheckUserRateLimit(http, cfg, "image-test") is { } limited) return limited;
            return await RenderImageCode(assignmentId, request, http, db, clients, cfg);
        });

        app.MapPost("/api/image-assignments/{assignmentId:guid}/compare-code", async (
            Guid assignmentId, ImageCodeRequest request, HttpContext http, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg) =>
        {
            if (CheckUserRateLimit(http, cfg, "image-test") is { } limited) return limited;
            return await CompareImageCode(assignmentId, request, db, clients, cfg, submit: false, context: http);
        });


        return app;
    }
}
