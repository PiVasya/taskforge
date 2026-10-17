using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TaskForge.Realtime;
using TaskForge.Solutions.Api.Contracts;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Services.Code;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private static WebApplication MapCodeSolutionsEndpoints(WebApplication app)
    {
        app.MapPost("/api/code-solutions", async (CodeSolutionSubmitRequest request, HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, AdminSolutionEventPublisher live, CancellationToken ct) =>
        {
            if (request.AssignmentId == Guid.Empty)
                return Problem(400, "ASSIGNMENT_ID_REQUIRED", "code-solutions.validation", "Для code solution требуется assignmentId.");
            return await CodeSolutionService.SubmitAsync(request.AssignmentId, new SubmitRequest(request.Language, request.Code, request.Input, null), http, cfg, db, httpFactory, live, ct);
        });

        app.MapGet("/api/code-solutions", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, Guid? assignmentId, int? days, int skip = 0, int take = 50, CancellationToken ct = default) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();
            var q = db.Submissions.AsNoTracking().Where(x => x.UserId == uid.Value && x.SqlSpecVersionId == null);
            if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
                q = q.Where(x => x.CreatedAt >= since);
            }
            var ordered = q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id);
            return StreamSolutionHistory(ordered, System.Math.Max(0, skip),
                System.Math.Clamp(take, 1, 200), false, IsEditor(http, cfg), cfg, httpFactory, ct);
        });

        app.MapGet("/api/code-solutions/{id:guid}", async (Guid id, HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();
            var row = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.SqlSpecVersionId == null, ct);
            if (row == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });
            if (row.UserId != uid.Value) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к этому решению.", code = "SOLUTION_FORBIDDEN" }, statusCode: 403);
            var metadata = await LoadAssignmentMetadataAsync(new[] { row.AssignmentId }, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(row, IsEditor(http, cfg), metadata.GetValueOrDefault(row.AssignmentId)));
        });

        app.MapGet("/api/code-solutions/top", async (Guid assignmentId, HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, int top = 20, CancellationToken ct = default) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();
            var isEditor = IsEditor(http, cfg);
            var contract = await LoadCodeSolutionContractAsync(assignmentId, uid.Value, cfg, httpFactory, ct);
            if (contract is null || (!isEditor && !contract.CanView))
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });

            var limit = System.Math.Clamp(top, 1, 100);
            var canViewCode = isEditor || await db.Submissions.AsNoTracking().AnyAsync(x => x.AssignmentId == assignmentId && x.UserId == uid.Value && x.Status == "Accepted" && x.SqlSpecVersionId == null, ct);
            var ordered = db.Submissions.AsNoTracking()
                .Where(x => x.AssignmentId == assignmentId && x.Status == "Accepted" && x.SqlSpecVersionId == null)
                .OrderByDescending(x => x.Score).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id);
            return Microsoft.AspNetCore.Http.Results.Stream(async stream =>
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartArray();
                var metadata = await LoadAssignmentMetadataAsync(new[] { assignmentId }, cfg, httpFactory, ct);
                for (var offset = 0; offset < limit; offset += 16)
                {
                    ct.ThrowIfCancellationRequested();
                    var wanted = System.Math.Min(16, limit - offset);
                    var batch = await ordered.Skip(offset).Take(wanted).ToListAsync(ct);
                    if (batch.Count == 0) break;
                    var users = await LoadUserSummariesAsync(
                        batch.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value), cfg, httpFactory, ct);
                    foreach (var row in batch)
                    {
                        var dto = ToTopSolutionDto(row, canViewCode || row.UserId == uid.Value,
                            metadata.GetValueOrDefault(row.AssignmentId),
                            row.UserId.HasValue ? users.GetValueOrDefault(row.UserId.Value) : null);
                        JsonSerializer.Serialize(writer, dto, JsonOptions());
                    }
                    await writer.FlushAsync(ct);
                    if (batch.Count < wanted) break;
                }
                writer.WriteEndArray();
                await writer.FlushAsync(ct);
            }, "application/json; charset=utf-8");
        });

        return app;
    }
}
