using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;

using TaskForge.Tasks.Api.Contracts;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapImageTestsEndpoints(WebApplication app)
    {
        app.MapPost("/api/assignments/{assignmentId:guid}/image-test/reference", async (Guid assignmentId, HttpRequest req, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (CheckUserRateLimit(http, cfg, "image-test") is { } limited) return limited;
            if (!IsEditor(http, cfg)) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Для загрузки эталона нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
            var assignment = await db.Assignments.FindAsync(assignmentId);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file == null || file.Length == 0) return Problem(400, "IMAGE_REFERENCE_REQUIRED", "request.validation", "Выберите эталонную картинку для image-test.");
            if (file.Length > MaxImageUploadBytes(cfg)) return Problem(413, "IMAGE_REFERENCE_TOO_LARGE", "request.validation", $"Эталонная картинка слишком большая. Максимум: {MaxImageUploadBytes(cfg) / 1024 / 1024} МБ.");
            var threshold = form.TryGetValue("threshold", out var t) && int.TryParse(t, out var tv) ? System.Math.Clamp(tv, 0, 100) : 90;
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var uploaded = await UploadImageBytesToFilesApiAsync(clients, cfg, ms.ToArray(), file.FileName, string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, $"image-tests/reference/{assignmentId:N}", ct);
            var payload = JsonNode.Parse(assignment.TestsJson ?? "{}") as JsonObject ?? new JsonObject();
            payload["imageTestReferenceKey"] = uploaded.Key;
            payload["imageTestReferenceUrl"] = uploaded.PrivateUrl;
            payload["imageTestSimilarityThreshold"] = threshold;
            payload["referenceFileName"] = uploaded.FileName;
            payload["referenceContentType"] = uploaded.ContentType;
            payload["referenceSize"] = uploaded.Size;
            RemoveInlineImageFields(payload);
            assignment.Type = "image-test";
            assignment.TestsJson = payload.ToJsonString(JsonOptions());
            assignment.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { assignmentId, key = uploaded.Key, url = uploaded.PrivateUrl, privateUrl = uploaded.PrivateUrl, threshold, storage = "minio" });
        }).DisableAntiforgery();

        app.MapPost("/api/assignments/{assignmentId:guid}/image-test/compare", async (Guid assignmentId, HttpRequest req, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients) =>
        {
            if (CheckUserRateLimit(http, cfg, "image-test") is { } limited) return limited;
            return await CompareImageUpload(assignmentId, req, http, cfg, db, clients);
        });

        app.MapPost("/api/assignments/{assignmentId:guid}/image-test/run-code", async (Guid assignmentId, ImageCodeRequest request, HttpContext http, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg) =>
        {
            if (CheckUserRateLimit(http, cfg, "image-test") is { } limited) return limited;
            return await RenderImageCode(assignmentId, request, http, db, clients, cfg);
        });

        app.MapPost("/api/assignments/{assignmentId:guid}/image-test/compare-code", async (Guid assignmentId, ImageCodeRequest request, HttpContext http, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg) =>
        {
            if (CheckUserRateLimit(http, cfg, "image-test") is { } limited) return limited;
            return await CompareImageCode(assignmentId, request, db, clients, cfg, submit: false, context: http);
        });

        app.MapPost("/api/assignments/{assignmentId:guid}/image-test/submit-code", async (Guid assignmentId, ImageCodeRequest request, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients) =>
        {
            if (CheckUserRateLimit(http, cfg, "image-test") is { } limited) return limited;
            return await CompareImageCode(assignmentId, request, db, clients, cfg, submit: true, context: http);
        });

        return app;
    }
}
