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
    private static WebApplication MapAssignmentsEndpoints(WebApplication app)
    {
        app.MapGet("/api/courses/{courseId:guid}/assignments", async (Guid courseId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var includeHidden = IsEditor(http, cfg);
            if (!includeHidden && !await CanUserAccessCourseAsync(courseId, http, cfg, clients, ct))
            {
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден.", code = "COURSE_NOT_FOUND" });
            }

            var query = db.Assignments.AsNoTracking().Where(x => x.CourseId == courseId);
            if (!includeHidden) query = query.Where(x => x.IsVisible);
            var rows = await query.OrderBy(x => x.Sort).ThenBy(x => x.CreatedAt).ToListAsync(ct);

            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            var solvedIds = userId.HasValue
                ? await LoadSolvedAssignmentIdsAsync(userId.Value, rows.Select(x => x.Id), db, clients, cfg, ct)
                : new HashSet<Guid>();

            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToDto(x, includeHidden, solvedIds.Contains(x.Id))).ToList());
        });

        app.MapGet("/api/courses/{courseId:guid}/assignments/export-json", async (Guid courseId, HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            if (!IsEditor(http, cfg))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Для экспорта заданий нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var rows = await db.Assignments.AsNoTracking()
                .Where(x => x.CourseId == courseId)
                .OrderBy(x => x.Sort)
                .ThenBy(x => x.CreatedAt)
                .ToListAsync(ct);

            return Microsoft.AspNetCore.Http.Results.Json(new
            {
                schemaVersion = 1,
                format = "taskforge-course-assignment-import",
                courseId,
                exportedAt = DateTimeOffset.UtcNow,
                assignments = rows.Select(ToImportDto).ToList()
            }, JsonOptions());
        });

        app.MapPost("/api/courses/{courseId:guid}/assignments", async (Guid courseId, AssignmentRequest request, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
        {
            var maxSort = await db.Assignments.Where(x => x.CourseId == courseId).Select(x => (int?)x.Sort).MaxAsync(ct) ?? -1;
            var assignment = await BuildAssignmentEntityAsync(courseId, request, maxSort + 1, clients, cfg, ct);
            db.Assignments.Add(assignment);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        app.MapPost("/api/courses/{courseId:guid}/assignments/import-json", async (Guid courseId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!IsEditor(http, cfg))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Для импорта заданий нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var sourceItems = ExtractAssignmentImportItems(payload).ToList();
            if (sourceItems.Count == 0)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "JSON не содержит заданий. Передай объект задания, массив заданий или объект с полем assignments/items/tasks.", code = "IMPORT_EMPTY" }, statusCode: StatusCodes.Status400BadRequest);
            }
            if (sourceItems.Count > 200)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "За один импорт можно обработать не больше 200 заданий.", code = "IMPORT_TOO_LARGE", count = sourceItems.Count }, statusCode: StatusCodes.Status400BadRequest);
            }

            var requests = new List<AssignmentRequest>();
            var issues = new List<object>();
            for (var i = 0; i < sourceItems.Count; i++)
            {
                try
                {
                    var req = AssignmentRequestFromJson(sourceItems[i]);
                    var itemIssues = ValidateImportedAssignment(req, i + 1).ToList();
                    if (itemIssues.Count > 0)
                    {
                        issues.Add(new { index = i + 1, id = req.Id, title = req.Title, issues = itemIssues });
                    }
                    requests.Add(req);
                }
                catch (Exception ex)
                {
                    issues.Add(new { index = i + 1, issues = new[] { $"Не удалось прочитать объект задания: {ex.Message}" } });
                }
            }

            if (issues.Count > 0)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Импорт остановлен: в JSON есть ошибки.", code = "IMPORT_VALIDATION_FAILED", issues }, statusCode: StatusCodes.Status400BadRequest);
            }

            var ids = requests.Select(x => x.Id).Where(x => x.HasValue && x.Value != Guid.Empty).Select(x => x!.Value).Distinct().ToList();
            var existingById = ids.Count == 0
                ? new Dictionary<Guid, Assignment>()
                : await db.Assignments.Where(x => x.CourseId == courseId && ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var usedIds = ids.Count == 0
                ? new HashSet<Guid>()
                : await db.Assignments.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToHashSetAsync(ct);

            var maxSort = await db.Assignments.Where(x => x.CourseId == courseId).Select(x => (int?)x.Sort).MaxAsync(ct) ?? -1;
            var created = new List<Assignment>();
            var updated = new List<Assignment>();
            var nextSort = maxSort + 1;

            foreach (var request in requests)
            {
                if (request.Id.HasValue && existingById.TryGetValue(request.Id.Value, out var existing))
                {
                    await ApplyAssignmentRequestAsync(existing, request, clients, cfg, ct);
                    updated.Add(existing);
                    continue;
                }

                var createRequest = request;
                if (createRequest.Id.HasValue && usedIds.Contains(createRequest.Id.Value))
                {
                    createRequest = createRequest with { Id = null };
                }

                var assignment = await BuildAssignmentEntityAsync(courseId, createRequest, nextSort++, clients, cfg, ct);
                created.Add(assignment);
            }

            if (created.Count > 0) db.Assignments.AddRange(created);
            await db.SaveChangesAsync(ct);

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                createdCount = created.Count,
                updatedCount = updated.Count,
                totalCount = created.Count + updated.Count,
                assignments = created.Concat(updated).Select(x => ToDto(x, includeSensitive: true)).ToList()
            });
        });

        app.MapGet("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            var includeSensitive = IsEditor(http, cfg);
            if (!includeSensitive && !await CanUserAccessAssignmentAsync(assignment, http, cfg, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive));
        });

        app.MapGet("/api/assignments/{assignmentId:guid}/edit", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db) =>
        {
            if (!IsEditor(http, cfg)) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Для редактирования нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
            return assignment == null ? Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" }) : Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        app.MapPut("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, AssignmentRequest request, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.FindAsync(assignmentId);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            await ApplyAssignmentRequestAsync(assignment, request, clients, cfg, ct);
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        app.MapDelete("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, TasksDbContext db) =>
        {
            var assignment = await db.Assignments.FindAsync(assignmentId);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            db.Attempts.RemoveRange(await db.Attempts.Where(x => x.TaskAssignmentId == assignmentId).ToListAsync());
            db.Assignments.Remove(assignment);
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(new { message = "Задание удалено.", deleted = assignmentId });
        });

        app.MapPatch("/api/assignments/{assignmentId:guid}/sort", async (Guid assignmentId, SortRequest request, TasksDbContext db) =>
        {
            var assignment = await db.Assignments.FindAsync(assignmentId);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            assignment.Sort = request.Sort;
            assignment.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        app.MapPatch("/api/assignments/{assignmentId:guid}/position", async (Guid assignmentId, PositionRequest request, TasksDbContext db) =>
        {
            var assignment = await db.Assignments.FindAsync(assignmentId);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            var siblings = await db.Assignments.Where(x => x.CourseId == assignment.CourseId && x.Id != assignment.Id).OrderBy(x => x.Sort).ToListAsync();
            var pos = System.Math.Clamp((request.Position ?? siblings.Count + 1) - 1, 0, siblings.Count);
            siblings.Insert(pos, assignment);
            for (var i = 0; i < siblings.Count; i++) siblings[i].Sort = i;
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        app.MapPatch("/api/assignments/{assignmentId:guid}/visibility", async (Guid assignmentId, VisibilityRequest request, TasksDbContext db) =>
        {
            var assignment = await db.Assignments.FindAsync(assignmentId);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            assignment.IsVisible = request.IsVisible;
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        return app;
    }
}
