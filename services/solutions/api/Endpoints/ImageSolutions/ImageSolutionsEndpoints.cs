using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

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
    private static WebApplication MapImageSolutionsEndpoints(WebApplication app)
    {
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

        app.MapGet("/api/admin/users/{userId:guid}/image-solutions", async (Guid userId, SolutionsDbContext db, Guid? assignmentId, int? days, int skip = 0, int take = 50) =>
        {
            var q = db.ImageSolutions.AsNoTracking().Where(x => x.UserId == userId);
            if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
                q = q.Where(x => x.CreatedAt >= since);
            }
            var rows = await q.OrderByDescending(x => x.CreatedAt).Skip(System.Math.Max(0, skip)).Take(System.Math.Clamp(take, 1, 200)).ToListAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ImageDto(x, includeReference: true)).ToList());
        });

        app.MapPost("/api/internal/image-solutions", async (InternalImageSolutionRequest request, SolutionsDbContext db, CancellationToken ct) =>
        {
            if (request.UserId == Guid.Empty || request.AssignmentId == Guid.Empty)
            {
                return Problem(400, "IMAGE_SOLUTION_INVALID_REQUEST", "image-solutions.validation", "Не хватает userId или assignmentId для сохранения image-решения.");
            }

            var row = new UserImageTaskSolution
            {
                UserId = request.UserId,
                AssignmentId = request.AssignmentId,
                Language = NormalizeLanguage(request.Language) ?? "text",
                Code = request.Code ?? string.Empty,
                SimilarityPercent = System.Math.Clamp(request.SimilarityPercent, 0, 100),
                Passed = request.Passed,
                ResultJson = request.Result.HasValue
                    ? request.Result.Value.GetRawText()
                    : JsonSerializer.Serialize(new { passed = request.Passed, similarityPercent = request.SimilarityPercent }, JsonOptions())
            };
            db.ImageSolutions.Add(row);
            await MarkRatingDirtyAsync(db, row.UserId, "image-solution", row.AssignmentId, ct);
            await db.SaveChangesAsync(ct);
            TaskForgeDebugTrace.Map("IMAGE_SOLUTION_SAVED",
                ("solution", row.Id),
                ("user", row.UserId),
                ("assignment", row.AssignmentId),
                ("language", row.Language),
                ("passed", row.Passed),
                ("similarityPercent", row.SimilarityPercent),
                ("codeHash", TaskForgeDebugTrace.Fingerprint(row.Code)));
            return Microsoft.AspNetCore.Http.Results.Ok(ImageDto(row, includeReference: false));
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
