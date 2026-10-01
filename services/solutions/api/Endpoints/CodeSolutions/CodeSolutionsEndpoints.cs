using Microsoft.EntityFrameworkCore;
using TaskForge.Realtime;
using TaskForge.Solutions.Api.Contracts;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Services.Code;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;

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
            var rows = await q.OrderByDescending(x => x.CreatedAt).Skip(System.Math.Max(0, skip)).Take(System.Math.Clamp(take, 1, 200)).ToListAsync(ct);
            var metadata = await LoadAssignmentMetadataAsync(rows.Select(x => x.AssignmentId), cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToDto(x, IsEditor(http, cfg), metadata.GetValueOrDefault(x.AssignmentId))).ToList());
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
            var rows = await db.Submissions.AsNoTracking()
                .Where(x => x.AssignmentId == assignmentId && x.Status == "Accepted" && x.SqlSpecVersionId == null)
                .OrderByDescending(x => x.Score).ThenBy(x => x.CreatedAt).Take(limit).ToListAsync(ct);
            var metadata = await LoadAssignmentMetadataAsync(new[] { assignmentId }, cfg, httpFactory, ct);
            var users = await LoadUserSummariesAsync(rows.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value), cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToTopSolutionDto(x, canViewCode || x.UserId == uid.Value, metadata.GetValueOrDefault(x.AssignmentId), x.UserId.HasValue ? users.GetValueOrDefault(x.UserId.Value) : null)).ToList());
        });

        return app;
    }
}
