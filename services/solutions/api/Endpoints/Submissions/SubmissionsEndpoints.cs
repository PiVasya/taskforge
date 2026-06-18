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
    private static WebApplication MapSubmissionsEndpoints(WebApplication app)
    {
        app.MapPost("/api/assignments/{assignmentId:guid}/submit", async (Guid assignmentId, SubmitRequest request, HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (CheckUserRateLimit(http, "solution-submit") is { } limited) return limited;
            var userId = CurrentUserId(http, cfg);
            if (userId == null) return Unauthorized();
            var canRevealHidden = IsEditor(http, cfg);

            var language = NormalizeLanguage(request.Language) ?? "csharp";
            var code = request.Code ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code))
            {
                return Problem(400, "SOLUTION_CODE_REQUIRED", "solutions.validation", "Нельзя отправить пустое решение.");
            }

            if (!canRevealHidden)
            {
                var access = await LoadAssignmentAccessAsync(assignmentId, userId.Value, cfg, httpFactory, ct);
                if (access?.CanSubmit != true)
                {
                    return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
                }
            }

            var sub = new SolutionSubmission
            {
                AssignmentId = assignmentId,
                UserId = userId,
                Language = language,
                Code = code,
                Status = "Preparing",
                Score = 0,
                ResultJson = JsonSerializer.Serialize(new { verdict = "Preparing", message = "Решение принято и готовится к проверке." }, JsonOptions())
            };
            db.Submissions.Add(sub);
            await db.SaveChangesAsync(ct);

            var spec = await LoadJudgeSpecAsync(assignmentId, cfg, httpFactory, ct);
            if (spec == null)
            {
                ApplyLocalVerdict(sub, new JudgeRunResult(
                    "JudgeUnavailable",
                    0,
                    false,
                    false,
                    "Не удалось получить тесты задания из tasks-api. Проверьте, что tasks-api доступен и внутренний ключ совпадает.",
                    null,
                    null,
                    false));
                await db.SaveChangesAsync(ct);
                return Microsoft.AspNetCore.Http.Results.Ok(ToSubmitDto(sub, canRevealHidden));
            }

            if (!IsAllowedLanguage(language, spec))
            {
                ApplyLocalVerdict(sub, new JudgeRunResult(
                    "LanguageNotAllowed",
                    0,
                    false,
                    false,
                    $"Язык {language} не разрешён для этого задания.",
                    null,
                    CloneJson(JsonSerializer.Serialize(new { language, allowedLanguages = EffectiveAllowedLanguages(spec) }, JsonOptions())),
                    false));
                await db.SaveChangesAsync(ct);
                return Microsoft.AspNetCore.Http.Results.Ok(ToSubmitDto(sub, canRevealHidden));
            }
            var tests = ExtractTests(spec);
            if (tests.Length == 0)
            {
                ApplyLocalVerdict(sub, new JudgeRunResult(
                    "NoTestsConfigured",
                    0,
                    false,
                    false,
                    "Для задания не настроены тесты, поэтому решение не может быть зачтено автоматически.",
                    null,
                    null,
                    false));
                await db.SaveChangesAsync(ct);
                return Microsoft.AspNetCore.Http.Results.Ok(ToSubmitDto(sub, canRevealHidden));
            }

            var enqueue = await EnqueueExecutionJobAsync(sub.Id, assignmentId, userId.Value, language, code, request.Input, tests, spec, cfg, httpFactory, ct);
            if (!enqueue.Created)
            {
                ApplyLocalVerdict(sub, new JudgeRunResult(
                    "JudgeUnavailable",
                    0,
                    false,
                    false,
                    enqueue.Message ?? "Execution pipeline временно недоступен.",
                    null,
                    enqueue.Raw,
                    false));
                await db.SaveChangesAsync(ct);
                return Microsoft.AspNetCore.Http.Results.Ok(ToSubmitDto(sub, canRevealHidden));
            }

            sub.Status = "Queued";
            sub.Score = 0;
            sub.ResultJson = JsonSerializer.Serialize(new
            {
                verdict = "Queued",
                status = "Queued",
                pending = true,
                executionJobId = enqueue.JobId,
                message = "Решение поставлено в очередь проверки. Результат появится после обработки execution-worker."
            }, JsonOptions());
            await db.SaveChangesAsync(ct);

            var final = await WaitForTerminalSubmissionAsync(db, sub.Id, cfg, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToSubmitDto(final ?? sub, canRevealHidden));
        });

        app.MapGet("/api/assignments/{assignmentId:guid}/top-solutions", async (Guid assignmentId, HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, int top = 20, CancellationToken ct = default) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();

            var isEditor = IsEditor(http, cfg);
            if (!isEditor)
            {
                var access = await LoadAssignmentAccessAsync(assignmentId, uid.Value, cfg, httpFactory, ct);
                if (access?.CanView != true)
                {
                    return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
                }
            }

            var limit = System.Math.Clamp(top, 1, 100);
            var canViewCode = isEditor || await db.Submissions.AsNoTracking().AnyAsync(x => x.AssignmentId == assignmentId && x.UserId == uid.Value && x.Status == "Accepted", ct);
            var rows = await db.Submissions.AsNoTracking()
                .Where(x => x.AssignmentId == assignmentId && x.Status == "Accepted")
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.CreatedAt)
                .Take(limit)
                .ToListAsync(ct);
            var metadata = await LoadAssignmentMetadataAsync(new[] { assignmentId }, cfg, httpFactory, ct);
            var users = await LoadUserSummariesAsync(rows.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value), cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToTopSolutionDto(x, canViewCode || x.UserId == uid.Value, metadata.GetValueOrDefault(x.AssignmentId), x.UserId.HasValue ? users.GetValueOrDefault(x.UserId.Value) : null)).ToList());
        });

        app.MapGet("/api/me/solutions", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, Guid? assignmentId, int? days, int skip = 0, int take = 50, CancellationToken ct = default) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();

            var q = db.Submissions.AsNoTracking().Where(x => x.UserId == uid.Value);
            if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
                q = q.Where(x => x.CreatedAt >= since);
            }

            var rows = await q
                .OrderByDescending(x => x.CreatedAt)
                .Skip(System.Math.Max(0, skip))
                .Take(System.Math.Clamp(take, 1, 200))
                .ToListAsync(ct);
            var includeHiddenDetails = IsEditor(http, cfg);
            var metadata = await LoadAssignmentMetadataAsync(rows.Select(x => x.AssignmentId), cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToDto(x, includeHiddenDetails, metadata.GetValueOrDefault(x.AssignmentId))).ToList());
        });

        app.MapGet("/api/me/solutions/{id:guid}", async (Guid id, HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();

            var s = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });
            if (s.UserId != uid.Value) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к этому решению.", code = "SOLUTION_FORBIDDEN" }, statusCode: 403);
            var metadata = await LoadAssignmentMetadataAsync(new[] { s.AssignmentId }, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(s, includeSensitiveResult: IsEditor(http, cfg), metadata: metadata.GetValueOrDefault(s.AssignmentId)));
        });

        app.MapGet("/api/admin/solutions/{id:guid}", async (Guid id, SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var s = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });
            var metadata = await LoadAssignmentMetadataAsync(new[] { s.AssignmentId }, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(s, includeSensitiveResult: true, metadata: metadata.GetValueOrDefault(s.AssignmentId)));
        });

        app.MapDelete("/api/admin/solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => { var s = await db.Submissions.FindAsync(id); if (s == null) return Microsoft.AspNetCore.Http.Results.NotFound(); db.Submissions.Remove(s); await db.SaveChangesAsync(); return Microsoft.AspNetCore.Http.Results.Ok(new { deleted = id }); });

        app.MapGet("/api/admin/users/{userId:guid}/solutions", async (Guid userId, SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, Guid? assignmentId, int? days, int skip = 0, int take = 50, CancellationToken ct = default) =>
        {
            var q = db.Submissions.AsNoTracking().Where(x => x.UserId == userId);
            if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
                q = q.Where(x => x.CreatedAt >= since);
            }

            var rows = await q
                .OrderByDescending(x => x.CreatedAt)
                .Skip(System.Math.Max(0, skip))
                .Take(System.Math.Clamp(take, 1, 200))
                .ToListAsync(ct);
            var metadata = await LoadAssignmentMetadataAsync(rows.Select(x => x.AssignmentId), cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToDto(x, includeSensitiveResult: true, metadata: metadata.GetValueOrDefault(x.AssignmentId))).ToList());
        });

        app.MapDelete("/api/admin/users/{userId:guid}/solutions", async (Guid userId, SolutionsDbContext db, Guid? assignmentId, int? days) =>
        {
            var q = db.Submissions.Where(x => x.UserId == userId);
            if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
                q = q.Where(x => x.CreatedAt >= since);
            }
            var rows = await q.ToListAsync();
            db.Submissions.RemoveRange(rows);
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(new { deleted = rows.Count, assignmentId, days });
        });

        app.MapGet("/api/admin/solution-users", async (SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, string? q, int take = 200, CancellationToken ct = default) =>
        {
            var ratingRows = await db.UserRatings.AsNoTracking().OrderByDescending(x => x.TotalScore).Take(1000).ToListAsync(ct);
            var ids = ratingRows.Select(x => x.UserId)
                .Concat(await db.Submissions.AsNoTracking().Where(x => x.UserId.HasValue).Select(x => x.UserId.Value).Distinct().Take(1000).ToListAsync(ct))
                .Distinct()
                .Take(1000)
                .ToArray();
            var users = await LoadUserSummariesAsync(ids, cfg, httpFactory, ct);
            var ratings = ratingRows.ToDictionary(x => x.UserId);
            var search = NormalizeSearch(q);
            var rows = ids
                .Select(id => new { Id = id, User = users.GetValueOrDefault(id), Rating = ratings.GetValueOrDefault(id) })
                .Where(x => string.IsNullOrWhiteSpace(search) || UserSummarySearchScore(x.User, x.Id, search) <= System.Math.Max(1, System.Math.Min(4, search.Length / 3)) || UserSummaryHaystack(x.User, x.Id).Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Rating?.TotalScore ?? 0)
                .ThenBy(x => UserLabel(x.User))
                .Take(System.Math.Clamp(take, 1, 500))
                .Select(x => new
                {
                    id = x.Id,
                    userId = x.Id,
                    login = x.User?.Login,
                    email = x.User?.Email ?? x.User?.MaskedEmail,
                    maskedEmail = x.User?.MaskedEmail,
                    displayName = UserLabel(x.User),
                    fullName = UserLabel(x.User),
                    firstName = x.User?.FirstName,
                    lastName = x.User?.LastName,
                    score = x.Rating?.TotalScore ?? 0,
                    solved = x.Rating?.SolvedCount ?? 0
                })
                .ToList();
            return Microsoft.AspNetCore.Http.Results.Ok(rows);
        });

        return app;
    }
}
