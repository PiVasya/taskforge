using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Access;

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
            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!includeHidden)
            {
                if (!userId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
                var evaluation = await CourseMapProgressionService.LoadEvaluationAsync(courseId, userId.Value, db, clients, cfg, ct);
                if (evaluation == null)
                    return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден.", code = "COURSE_NOT_FOUND" });

                var visibleIds = evaluation.VisibleAssignmentIds;
                var rows = await db.Assignments.AsNoTracking()
                    .Where(x => x.CourseId == courseId && visibleIds.Contains(x.Id))
                    .OrderBy(x => x.Sort)
                    .ThenBy(x => x.CreatedAt)
                    .ToListAsync(ct);
                return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToDto(x, false, evaluation.SolvedAssignmentIds.Contains(x.Id))).ToList());
            }

            var editorRows = await db.Assignments.AsNoTracking()
                .Where(x => x.CourseId == courseId)
                .OrderBy(x => x.Sort)
                .ThenBy(x => x.CreatedAt)
                .ToListAsync(ct);
            var editorSolvedIds = userId.HasValue
                ? await LoadSolvedAssignmentIdsAsync(userId.Value, editorRows.Select(x => x.Id), db, clients, cfg, ct)
                : new HashSet<Guid>();
            return Microsoft.AspNetCore.Http.Results.Ok(editorRows.Select(x => ToDto(x, true, editorSolvedIds.Contains(x.Id))).ToList());
        });


        app.MapGet("/api/courses/{courseId:guid}/learning-map", async (Guid courseId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!userId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var evaluation = await CourseMapProgressionService.LoadEvaluationAsync(courseId, userId.Value, db, clients, cfg, ct);
            if (evaluation == null)
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден или ещё не открыт.", code = "COURSE_NOT_AVAILABLE" });

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                rootCourseId = evaluation.RootCourseId,
                requestedCourseId = evaluation.RequestedCourseId,
                version = evaluation.Version,
                document = evaluation.LearningDocument,
                updatedAt = evaluation.UpdatedAt,
                updatedBy = evaluation.UpdatedBy
            });
        });


        app.MapGet("/api/courses/{courseId:guid}/assignments/tree", async (Guid courseId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var includeHidden = IsEditor(http, cfg);
            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!includeHidden)
            {
                if (!userId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
                var evaluation = await CourseMapProgressionService.LoadEvaluationAsync(courseId, userId.Value, db, clients, cfg, ct);
                if (evaluation == null)
                    return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден.", code = "COURSE_NOT_FOUND" });

                var visibleIds = evaluation.VisibleAssignmentIds;
                var rows = await db.Assignments.AsNoTracking()
                    .Where(x => evaluation.RequestedSubtreeCourseIds.Contains(x.CourseId) && visibleIds.Contains(x.Id))
                    .OrderBy(x => x.CourseId)
                    .ThenBy(x => x.Sort)
                    .ThenBy(x => x.CreatedAt)
                    .ToListAsync(ct);
                return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToDto(x, false, evaluation.SolvedAssignmentIds.Contains(x.Id))).ToList());
            }

            var tree = await GetInternalAsync<CourseTreeResponse>(
                clients,
                cfg,
                ServiceUrl(cfg, "EducationApi", "http://education-api:8080"),
                $"/api/internal/courses/{courseId:D}/tree",
                ct);
            if (tree == null || tree.CourseIds.Length == 0 || tree.Courses.All(x => x.Id != courseId))
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Не удалось получить дерево курса.", code = "COURSE_TREE_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var requestedIds = tree.CourseIds.Where(x => x != Guid.Empty).Distinct().Take(5000).ToArray();
            var editorRows = await db.Assignments.AsNoTracking()
                .Where(x => requestedIds.Contains(x.CourseId))
                .OrderBy(x => x.CourseId)
                .ThenBy(x => x.Sort)
                .ThenBy(x => x.CreatedAt)
                .ToListAsync(ct);
            var editorSolvedIds = userId.HasValue
                ? await LoadSolvedAssignmentIdsAsync(userId.Value, editorRows.Select(x => x.Id), db, clients, cfg, ct)
                : new HashSet<Guid>();
            return Microsoft.AspNetCore.Http.Results.Ok(editorRows.Select(x => ToDto(x, true, editorSolvedIds.Contains(x.Id))).ToList());
        });


        app.MapPost("/api/assignments/course-progress", async (CourseIdsRequest request, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var requestedIds = (request.CourseIds ?? Array.Empty<Guid>())
                .Where(x => x != Guid.Empty)
                .Distinct()
                .Take(2000)
                .ToArray();

            if (requestedIds.Length == 0)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<CourseAssignmentProgressDto>());
            }

            var includeHidden = IsEditor(http, cfg);
            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            var allowedIds = requestedIds;
            var accessByCourseId = new Dictionary<Guid, CourseAccessDto>();
            var progressionByRootId = new Dictionary<Guid, CourseMapProgressionService.Evaluation?>();

            if (!includeHidden)
            {
                if (!userId.HasValue)
                {
                    return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<CourseAssignmentProgressDto>());
                }

                var accessRows = await LoadCourseAccessRowsAsync(requestedIds, userId.Value, clients, cfg, ct);
                accessByCourseId = accessRows
                    .Where(x => x.CanView)
                    .GroupBy(x => x.CourseId)
                    .ToDictionary(x => x.Key, x => x.First());

                // Progress counters must obey the same graph gates as the map itself.
                // Otherwise a fully hidden branch could still leak its number of tasks
                // through the X/Y badge on the course card. Evaluate once per distinct
                // root map, not once per requested child course.
                var gatedRootIds = accessByCourseId.Values
                    .Where(x => x.HasProgressionRules && x.RootCourseId != Guid.Empty)
                    .Select(x => x.RootCourseId)
                    .Distinct()
                    .ToArray();

                foreach (var rootCourseId in gatedRootIds)
                {
                    progressionByRootId[rootCourseId] = await CourseMapProgressionService.LoadEvaluationAsync(
                        rootCourseId,
                        userId.Value,
                        db,
                        clients,
                        cfg,
                        ct);
                }

                allowedIds = requestedIds
                    .Where(id => accessByCourseId.TryGetValue(id, out var courseAccess)
                        && (!courseAccess.HasProgressionRules
                            || (progressionByRootId.TryGetValue(courseAccess.RootCourseId, out var evaluation)
                                && evaluation?.VisibleCourseIds.Contains(id) == true)))
                    .ToArray();
            }

            if (allowedIds.Length == 0)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<CourseAssignmentProgressDto>());
            }

            var query = db.Assignments.AsNoTracking().Where(x => allowedIds.Contains(x.CourseId));
            if (!includeHidden) query = query.Where(x => x.IsVisible);

            var rows = await query
                .Select(x => new { x.Id, x.CourseId })
                .ToListAsync(ct);

            if (!includeHidden)
            {
                rows = rows.Where(row =>
                {
                    if (!accessByCourseId.TryGetValue(row.CourseId, out var courseAccess) || !courseAccess.HasProgressionRules)
                        return true;
                    return progressionByRootId.TryGetValue(courseAccess.RootCourseId, out var evaluation)
                        && evaluation?.VisibleAssignmentIds.Contains(row.Id) == true;
                }).ToList();
            }

            var solvedIds = userId.HasValue
                ? await LoadSolvedAssignmentIdsAsync(userId.Value, rows.Select(x => x.Id), db, clients, cfg, ct)
                : new HashSet<Guid>();

            var grouped = rows
                .GroupBy(x => x.CourseId)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var total = g.Count();
                        var solved = g.Count(x => solvedIds.Contains(x.Id));
                        var percent = total > 0 ? (int)System.Math.Round((double)solved / total * 100) : 0;
                        return new CourseAssignmentProgressDto(g.Key, total, solved, percent, total > 0 && solved == total);
                    });

            return Microsoft.AspNetCore.Http.Results.Ok(allowedIds.Select(id =>
                grouped.TryGetValue(id, out var progress)
                    ? progress
                    : new CourseAssignmentProgressDto(id, 0, 0, 0, false)).ToList());
        });


        app.MapGet("/api/courses/{courseId:guid}/assignments/export-json", async (Guid courseId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!IsEditor(http, cfg))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Для экспорта заданий нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var tree = await GetInternalAsync<CourseTreeResponse>(
                clients,
                cfg,
                ServiceUrl(cfg, "EducationApi", "http://education-api:8080"),
                $"/api/internal/courses/{courseId:D}/tree",
                ct);

            if (tree == null || tree.CourseIds.Length == 0 || tree.Courses.All(x => x.Id != courseId))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Не удалось получить дерево курса для экспорта.", code = "COURSE_TREE_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var courseIds = tree.CourseIds.Where(x => x != Guid.Empty).Distinct().ToArray();
            var rows = await db.Assignments.AsNoTracking()
                .Where(x => courseIds.Contains(x.CourseId))
                .OrderBy(x => x.CourseId)
                .ThenBy(x => x.Sort)
                .ThenBy(x => x.CreatedAt)
                .ToListAsync(ct);

            var assignmentsByCourse = rows
                .GroupBy(x => x.CourseId)
                .ToDictionary(g => g.Key, g => g.Select(ToImportDto).Cast<object>().ToList());
            var coursesById = tree.Courses.ToDictionary(x => x.Id);
            var childrenByParent = tree.Courses
                .Where(x => x.ParentCourseId.HasValue && coursesById.ContainsKey(x.ParentCourseId.Value))
                .GroupBy(x => x.ParentCourseId!.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.Sort).ThenBy(x => x.Title).Select(x => x.Id).ToList());

            CourseAssignmentExportNode BuildCourseNode(Guid id)
            {
                var source = coursesById[id];
                var node = new CourseAssignmentExportNode
                {
                    Id = source.Id,
                    ParentCourseId = id == courseId ? null : source.ParentCourseId,
                    Title = source.Title,
                    Description = source.Description,
                    IsPublic = source.IsPublic,
                    Sort = source.Sort,
                    Assignments = assignmentsByCourse.TryGetValue(id, out var assignments) ? assignments : new List<object>()
                };

                if (childrenByParent.TryGetValue(id, out var childIds))
                {
                    node.Courses = childIds.Select(BuildCourseNode).ToList();
                }

                return node;
            }

            var rootAssignments = assignmentsByCourse.TryGetValue(courseId, out var rootItems) ? rootItems : new List<object>();
            return Microsoft.AspNetCore.Http.Results.Json(new
            {
                schemaVersion = 2,
                format = "taskforge-course-assignment-import",
                courseId,
                exportedAt = DateTimeOffset.UtcNow,
                assignments = rootAssignments,
                courseCount = courseIds.Length,
                assignmentCount = rows.Count,
                courseTree = BuildCourseNode(courseId)
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
            var ratingAffectedAssignmentIds = new HashSet<Guid>();
            var nextSort = maxSort + 1;

            foreach (var request in requests)
            {
                if (request.Id.HasValue && existingById.TryGetValue(request.Id.Value, out var existing))
                {
                    var oldRating = existing.Rating;
                    var oldVisible = existing.IsVisible;
                    await ApplyAssignmentRequestAsync(existing, request, clients, cfg, ct);
                    if (oldRating != existing.Rating || oldVisible != existing.IsVisible) ratingAffectedAssignmentIds.Add(existing.Id);
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

            foreach (var affectedId in ratingAffectedAssignmentIds)
            {
                var users = await db.Attempts.AsNoTracking()
                    .Where(x => x.TaskAssignmentId == affectedId)
                    .Select(x => x.UserId)
                    .Distinct()
                    .ToListAsync(ct);
                await MarkAssignmentRatingDirtyInSolutionsAsync(clients, cfg, affectedId, users, "assignment-import-updated", ct);
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                createdCount = created.Count,
                updatedCount = updated.Count,
                totalCount = created.Count + updated.Count,
                assignments = created.Concat(updated).Select(x => ToDto(x, includeSensitive: true)).ToList()
            });
        });

        app.MapGet("/api/assignments/{assignmentId:guid}/solve-shell", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            var includeSensitive = IsEditor(http, cfg);
            if (!includeSensitive && !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });

            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            var solved = userId.HasValue
                ? await LoadSolvedAssignmentIdsAsync(userId.Value, new[] { assignment.Id }, db, clients, cfg, ct)
                : new HashSet<Guid>();

            return Microsoft.AspNetCore.Http.Results.Ok(ToSolveShellDto(assignment, includeSensitive, solved.Contains(assignment.Id)));
        });

        app.MapGet("/api/assignments/{assignmentId:guid}/statement", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            var includeSensitive = IsEditor(http, cfg);
            if (!includeSensitive && !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            return Microsoft.AspNetCore.Http.Results.Ok(ToSolveStatementDto(assignment, includeSensitive));
        });

        app.MapGet("/api/assignments/{assignmentId:guid}/tests", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            var includeSensitive = IsEditor(http, cfg);
            if (!includeSensitive && !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            return Microsoft.AspNetCore.Http.Results.Ok(ToSolveTestsDto(assignment, includeSensitive));
        });

        app.MapGet("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            var includeSensitive = IsEditor(http, cfg);
            if (!includeSensitive && !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
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
            var oldRating = assignment.Rating;
            var oldVisible = assignment.IsVisible;
            await ApplyAssignmentRequestAsync(assignment, request, clients, cfg, ct);
            var affectsRating = oldRating != assignment.Rating || oldVisible != assignment.IsVisible;
            await db.SaveChangesAsync(ct);
            if (affectsRating)
            {
                var users = await db.Attempts.AsNoTracking()
                    .Where(x => x.TaskAssignmentId == assignmentId)
                    .Select(x => x.UserId)
                    .Distinct()
                    .ToListAsync(ct);
                await MarkAssignmentRatingDirtyInSolutionsAsync(clients, cfg, assignmentId, users, "assignment-updated", ct);
            }
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        app.MapDelete("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.FindAsync([assignmentId], ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            var attempts = await db.Attempts.Where(x => x.TaskAssignmentId == assignmentId).ToListAsync(ct);
            var users = attempts.Select(x => x.UserId).Where(x => x != Guid.Empty).Distinct().ToArray();
            db.Attempts.RemoveRange(attempts);
            db.Assignments.Remove(assignment);
            await db.SaveChangesAsync(ct);
            await MarkAssignmentRatingDirtyInSolutionsAsync(clients, cfg, assignmentId, users, "assignment-deleted", ct);
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

        app.MapPatch("/api/assignments/{assignmentId:guid}/visibility", async (Guid assignmentId, VisibilityRequest request, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.FindAsync([assignmentId], ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            var changed = assignment.IsVisible != request.IsVisible;
            assignment.IsVisible = request.IsVisible;
            await db.SaveChangesAsync(ct);
            if (changed)
            {
                var users = await db.Attempts.AsNoTracking()
                    .Where(x => x.TaskAssignmentId == assignmentId)
                    .Select(x => x.UserId)
                    .Distinct()
                    .ToListAsync(ct);
                await MarkAssignmentRatingDirtyInSolutionsAsync(clients, cfg, assignmentId, users, "assignment-visibility", ct);
            }
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        return app;
    }
}
