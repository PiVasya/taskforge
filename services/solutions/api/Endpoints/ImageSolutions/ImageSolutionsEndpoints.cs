using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;
using TaskForge.Realtime;

using TaskForge.Solutions.Api.Contracts;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Image.SolutionsApiImageService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    internal static IQueryable<UserImageTaskSolution> UserImageSolutionsForDeletion(IQueryable<UserImageTaskSolution> solutions, Guid userId)
        => solutions.Where(x => x.UserId == userId);

    private static WebApplication MapImageSolutionsEndpoints(WebApplication app)
    {
        app.MapPost("/api/image-solutions", async (ImageSolutionSubmitRequest request, HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory clients, AdminSolutionEventPublisher live, CancellationToken ct) =>
        {
            if (CheckUserRateLimit(http, cfg, "solution-submit") is { } limited) return limited;
            var userId = CurrentUserId(http, cfg);
            if (!userId.HasValue) return Unauthorized();
            if (request.AssignmentId == Guid.Empty || string.IsNullOrWhiteSpace(request.Code))
                return Problem(400, "IMAGE_SOLUTION_INVALID_REQUEST", "image-solutions.validation", "Для image solution требуются assignmentId и непустой код.");

            var client = clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(120);
            var tasksUrl = ServiceUrl(cfg, "TasksApi", "http://tasks-api:8080").TrimEnd('/');
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{tasksUrl}/api/internal/image-assignments/{request.AssignmentId:D}/evaluate-solution")
            {
                Content = JsonContent.Create(new { request.Language, request.Code, request.Input, request.TimeoutSeconds }, options: JsonOptions())
            };
            AddInternalKey(message, cfg);
            foreach (var name in new[] { "Authorization", "Cookie", "User-Agent", "X-Forwarded-For", "X-Real-IP" })
            {
                if (http.Request.Headers.TryGetValue(name, out var values))
                    message.Headers.TryAddWithoutValidation(name, values.ToArray());
            }

            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            foreach (var name in new[] { "X-Quota-Bucket", "X-Quota-Remaining", "X-Quota-Capacity", "X-Quota-Retry-After", "X-Quota-Next-Refill-At", "Retry-After" })
            {
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                    http.Response.Headers[name] = values.ToArray();
            }
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return Microsoft.AspNetCore.Http.Results.Content(raw, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);

            using var evaluated = JsonDocument.Parse(raw);
            var root = evaluated.RootElement;
            var passed = root.TryGetProperty("passed", out var passedNode) && passedNode.ValueKind == JsonValueKind.True;
            var similarity = root.TryGetProperty("similarityPercent", out var similarityNode) && similarityNode.TryGetDouble(out var similarityValue)
                ? (int)System.Math.Round(similarityValue)
                : 0;
            var language = NormalizeLanguage(request.Language) ?? "text";
            var row = new UserImageTaskSolution
            {
                UserId = userId.Value,
                AssignmentId = request.AssignmentId,
                Language = language,
                Code = request.Code ?? string.Empty,
                SimilarityPercent = System.Math.Clamp(similarity, 0, 100),
                Passed = passed,
                ResultJson = root.GetRawText()
            };
            db.ImageSolutions.Add(row);
            await MarkRatingDirtyAsync(db, row.UserId, "image-solution", row.AssignmentId, ct);
            await db.SaveChangesAsync(ct);
            await live.PublishAsync("image", row.Id, row.UserId, row.AssignmentId, row.Passed ? "Accepted" : "Rejected", row.SimilarityPercent);

            var output = JsonNode.Parse(root.GetRawText()) as JsonObject ?? new JsonObject();
            output["id"] = row.Id;
            output["solutionId"] = row.Id;
            output["assignmentId"] = row.AssignmentId;
            output["submitted"] = true;
            return Microsoft.AspNetCore.Http.Results.Json(output, JsonOptions());
        });

        app.MapGet("/api/me/image-solutions", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db, Guid? assignmentId, int? days, int skip = 0, int take = 50) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();
            var q = db.ImageSolutions.AsNoTracking().Where(x => x.UserId == uid.Value);
            if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
                q = q.Where(x => x.CreatedAt >= since);
            }
            var rows = await q.OrderByDescending(x => x.CreatedAt).Skip(System.Math.Max(0, skip)).Take(System.Math.Clamp(take, 1, 200)).ToListAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ImageDto(x, includeReference: false)).ToList());
        });

        app.MapGet("/api/me/image-solutions/{id:guid}", async (Guid id, HttpContext http, IConfiguration cfg, SolutionsDbContext db) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();
            var row = await db.ImageSolutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (row == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Решение не найдено.", code = "IMAGE_SOLUTION_NOT_FOUND" });
            if (row.UserId != uid.Value) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к этому решению.", code = "IMAGE_SOLUTION_FORBIDDEN" }, statusCode: 403);
            return Microsoft.AspNetCore.Http.Results.Ok(ImageDto(row, includeReference: false));
        });

        app.MapGet("/api/admin/image-solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => (await db.ImageSolutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)) is { } row ? Microsoft.AspNetCore.Http.Results.Ok(ImageDto(row, includeReference: true)) : Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Решение не найдено.", code = "IMAGE_SOLUTION_NOT_FOUND" }));

        app.MapGet("/api/admin/users/{userId:guid}/image-solutions", async (Guid userId, HttpContext http, SolutionsDbContext db, Guid? assignmentId, int? days, int skip = 0, int take = 50, CancellationToken ct = default) =>
        {
            var q = db.ImageSolutions.AsNoTracking().Where(x => x.UserId == userId);
            if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
                q = q.Where(x => x.CreatedAt >= since);
            }

            var total = await q.CountAsync(ct);
            http.Response.Headers["X-Total-Count"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
            http.Response.Headers["X-Result-User-Id"] = userId.ToString("D");

            var rows = await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip(System.Math.Max(0, skip)).Take(System.Math.Clamp(take, 1, 200)).ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ImageDto(x, includeReference: true)).ToList());
        });

        app.MapDelete("/api/admin/users/{userId:guid}/image-solutions", async (Guid userId, SolutionsDbContext db, CancellationToken ct) =>
        {
            if (userId == Guid.Empty)
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "USER_ID_REQUIRED" });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var deleted = await UserImageSolutionsForDeletion(db.ImageSolutions, userId).ExecuteDeleteAsync(ct);
            if (deleted > 0)
                await MarkRatingDirtyAsync(db, userId, "image-solutions-bulk-deleted", null, ct);
            await transaction.CommitAsync(ct);

            return Microsoft.AspNetCore.Http.Results.Ok(new { deleted, userId });
        });

        app.MapDelete("/api/admin/image-solutions/{id:guid}", async (Guid id, SolutionsDbContext db, CancellationToken ct) =>
        {
            var row = await db.ImageSolutions.FindAsync([id], ct);
            if (row == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            await MarkRatingDirtyAsync(db, row.UserId, "image-solution-deleted", row.AssignmentId, ct);
            db.ImageSolutions.Remove(row);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.NoContent();
        });

        return app;
    }
}
