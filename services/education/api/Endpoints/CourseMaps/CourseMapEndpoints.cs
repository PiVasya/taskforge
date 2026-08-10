using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Education.Api.Contracts;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;
using TaskForge.Education.Api.Hubs;
using static TaskForge.Education.Api.Services.Access.EducationApiAccessService;
using static TaskForge.Education.Api.Services.Common.EducationApiCommonService;

namespace TaskForge.Education.Api.Endpoints;

internal static partial class EducationApiEndpoints
{
    private static readonly HashSet<string> CourseMapNodeKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "course",
        "code-test",
        "test",
        "image-code",
        "math"
    };

    private static WebApplication MapCourseMapEndpoints(WebApplication app)
    {
        app.MapGet("/api/courses/{courseId:guid}/map", async (
            Guid courseId,
            HttpContext http,
            EducationDbContext db,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var requested = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == courseId, ct);
            if (requested is null || !CanViewCourse(access, requested)) return Microsoft.AspNetCore.Http.Results.NotFound();

            var root = await ResolveRootCourseAsync(courseId, db, ct);
            if (root is null || !CanViewCourse(access, root)) return Microsoft.AspNetCore.Http.Results.NotFound();

            var map = await db.CourseMaps.AsNoTracking().FirstOrDefaultAsync(x => x.RootCourseId == root.Id, ct);
            if (map is null)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(new CourseMapResponse(root.Id, courseId, 0, null, null, null));
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new CourseMapResponse(
                root.Id,
                courseId,
                map.Version,
                ParseDocumentElement(map.DocumentJson),
                map.UpdatedAt,
                map.UpdatedBy));
        });

        app.MapPut("/api/courses/{courseId:guid}/map", async (
            Guid courseId,
            CourseMapSaveRequest request,
            HttpContext http,
            EducationDbContext db,
            IConfiguration cfg,
            IHubContext<CourseMapPresenceHub> hub,
            CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var requested = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == courseId, ct);
            if (requested is null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден.", code = "COURSE_NOT_FOUND" });

            var root = await ResolveRootCourseAsync(courseId, db, ct);
            if (root is null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Корневой курс не найден.", code = "COURSE_ROOT_NOT_FOUND" });
            if (!CanEditCourse(access, root))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Недостаточно прав для сохранения карты курса.", code = "COURSE_MAP_EDIT_FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var validation = await ValidateCourseMapDocumentAsync(request.Document, root.Id, db, ct);
            if (validation is not null)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = validation.Value.Message, code = validation.Value.Code }, statusCode: StatusCodes.Status400BadRequest);
            }

            var normalizedJson = JsonSerializer.Serialize(request.Document, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var now = DateTimeOffset.UtcNow;
            var updatedBy = access.UserId.Value;
            var existing = await db.CourseMaps.AsNoTracking().FirstOrDefaultAsync(x => x.RootCourseId == root.Id, ct);
            int nextVersion;

            if (existing is null)
            {
                if (request.ExpectedVersion != 0)
                {
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { message = "Карта курса уже изменилась.", code = "COURSE_MAP_VERSION_CONFLICT", currentVersion = 0 });
                }

                var created = new CourseMap
                {
                    RootCourseId = root.Id,
                    DocumentJson = normalizedJson,
                    Version = 1,
                    UpdatedBy = updatedBy,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.CourseMaps.Add(created);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    var current = await db.CourseMaps.AsNoTracking()
                        .Where(x => x.RootCourseId == root.Id)
                        .Select(x => new { x.Version, x.UpdatedAt, x.UpdatedBy })
                        .FirstOrDefaultAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new
                    {
                        message = "Карта курса уже была сохранена другим редактором.",
                        code = "COURSE_MAP_VERSION_CONFLICT",
                        currentVersion = current?.Version ?? 0,
                        updatedAt = current?.UpdatedAt,
                        updatedBy = current?.UpdatedBy
                    });
                }
                nextVersion = 1;
            }
            else
            {
                if (existing.Version != request.ExpectedVersion)
                {
                    return Microsoft.AspNetCore.Http.Results.Conflict(new
                    {
                        message = "Карта курса уже изменилась другим редактором.",
                        code = "COURSE_MAP_VERSION_CONFLICT",
                        currentVersion = existing.Version,
                        updatedAt = existing.UpdatedAt,
                        updatedBy = existing.UpdatedBy
                    });
                }

                nextVersion = existing.Version + 1;
                var affected = await db.CourseMaps
                    .Where(x => x.Id == existing.Id && x.Version == request.ExpectedVersion)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.DocumentJson, normalizedJson)
                        .SetProperty(x => x.Version, nextVersion)
                        .SetProperty(x => x.UpdatedBy, updatedBy)
                        .SetProperty(x => x.UpdatedAt, now), ct);

                if (affected == 0)
                {
                    var current = await db.CourseMaps.AsNoTracking()
                        .Where(x => x.RootCourseId == root.Id)
                        .Select(x => new { x.Version, x.UpdatedAt, x.UpdatedBy })
                        .FirstOrDefaultAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new
                    {
                        message = "Карта курса уже изменилась другим редактором.",
                        code = "COURSE_MAP_VERSION_CONFLICT",
                        currentVersion = current?.Version ?? request.ExpectedVersion,
                        updatedAt = current?.UpdatedAt,
                        updatedBy = current?.UpdatedBy
                    });
                }
            }

            await hub.Clients.Group(CourseMapPresenceHub.Group(root.Id)).SendAsync("MapSaved", new
            {
                rootCourseId = root.Id,
                version = nextVersion,
                updatedAt = now,
                updatedBy
            }, ct);

            return Microsoft.AspNetCore.Http.Results.Ok(new CourseMapResponse(
                root.Id,
                courseId,
                nextVersion,
                request.Document.Clone(),
                now,
                updatedBy));
        });

        app.MapHub<CourseMapPresenceHub>("/hubs/course-map");

        return app;
    }

    private static async Task<Course?> ResolveRootCourseAsync(Guid requestedCourseId, EducationDbContext db, CancellationToken ct)
    {
        var rows = await db.Courses.AsNoTracking().Select(x => new { x.Id, x.ParentCourseId }).ToListAsync(ct);
        var parents = rows.ToDictionary(x => x.Id, x => x.ParentCourseId);
        if (!parents.ContainsKey(requestedCourseId)) return null;

        var current = requestedCourseId;
        var seen = new HashSet<Guid>();
        while (parents.TryGetValue(current, out var parent) && parent.HasValue)
        {
            if (!seen.Add(current)) return null;
            current = parent.Value;
        }

        return await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == current, ct);
    }

    private static async Task<HashSet<Guid>> LoadCourseSubtreeIdsAsync(Guid rootCourseId, EducationDbContext db, CancellationToken ct)
    {
        var rows = await db.Courses.AsNoTracking().Select(x => new { x.Id, x.ParentCourseId }).ToListAsync(ct);
        var children = rows
            .Where(x => x.ParentCourseId.HasValue)
            .GroupBy(x => x.ParentCourseId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToArray());

        var result = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(rootCourseId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!result.Add(id)) continue;
            if (!children.TryGetValue(id, out var childIds)) continue;
            foreach (var childId in childIds) stack.Push(childId);
        }
        return result;
    }

    private static async Task<(string Code, string Message)?> ValidateCourseMapDocumentAsync(JsonElement document, Guid rootCourseId, EducationDbContext db, CancellationToken ct)
    {
        if (document.ValueKind != JsonValueKind.Object) return ("COURSE_MAP_INVALID", "Документ карты должен быть JSON-объектом.");
        var byteLength = Encoding.UTF8.GetByteCount(document.GetRawText());
        if (byteLength > 5 * 1024 * 1024) return ("COURSE_MAP_TOO_LARGE", "Карта курса слишком большая.");

        if (!document.TryGetProperty("schemaVersion", out var schemaVersion)
            || schemaVersion.ValueKind != JsonValueKind.Number
            || !schemaVersion.TryGetInt32(out var schema)
            || schema != 1)
        {
            return ("COURSE_MAP_SCHEMA_UNSUPPORTED", "Поддерживается schemaVersion = 1.");
        }

        if (!document.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
            return ("COURSE_MAP_NODES_REQUIRED", "В карте отсутствует массив nodes.");
        if (!document.TryGetProperty("edges", out var edgesElement) || edgesElement.ValueKind != JsonValueKind.Array)
            return ("COURSE_MAP_EDGES_REQUIRED", "В карте отсутствует массив edges.");
        if (nodesElement.GetArrayLength() > 5000) return ("COURSE_MAP_TOO_MANY_NODES", "В карте не может быть больше 5000 узлов.");
        if (edgesElement.GetArrayLength() > 20000) return ("COURSE_MAP_TOO_MANY_EDGES", "В карте не может быть больше 20000 связей.");

        var subtreeCourseIds = await LoadCourseSubtreeIdsAsync(rootCourseId, db, ct);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var entityIds = new HashSet<Guid>();
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var hasRootCourseNode = false;

        foreach (var node in nodesElement.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) return ("COURSE_MAP_NODE_INVALID", "Каждый узел карты должен быть объектом.");
            var id = node.TryGetProperty("id", out var idElement) ? idElement.GetString()?.Trim() : null;
            var kind = node.TryGetProperty("type", out var typeElement) ? typeElement.GetString()?.Trim() : null;
            var entityRaw = node.TryGetProperty("entityId", out var entityElement) ? entityElement.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(id) || id.Length > 160 || !nodeIds.Add(id)) return ("COURSE_MAP_NODE_ID_INVALID", "Узлы карты должны иметь уникальные непустые id.");
            if (string.IsNullOrWhiteSpace(kind) || !CourseMapNodeKinds.Contains(kind)) return ("COURSE_MAP_NODE_TYPE_INVALID", $"Неизвестный тип узла: {kind ?? "(empty)"}.");
            if (!Guid.TryParse(entityRaw, out var entityId) || entityId == Guid.Empty) return ("COURSE_MAP_ENTITY_INVALID", "Узел карты должен ссылаться на корректный entityId.");
            if (!entityIds.Add(entityId)) return ("COURSE_MAP_ENTITY_DUPLICATE", "Одна сущность не может быть размещена на карте дважды.");
            if (string.Equals(kind, "course", StringComparison.OrdinalIgnoreCase) && !subtreeCourseIds.Contains(entityId))
                return ("COURSE_MAP_COURSE_OUTSIDE_TREE", "Карта содержит курс за пределами текущего дерева.");
            if (string.Equals(kind, "course", StringComparison.OrdinalIgnoreCase) && entityId == rootCourseId)
                hasRootCourseNode = true;

            if (!node.TryGetProperty("position", out var position) || position.ValueKind != JsonValueKind.Object)
                return ("COURSE_MAP_POSITION_REQUIRED", "Для каждого узла нужна position.");
            if (!TryFiniteCoordinate(position, "x") || !TryFiniteCoordinate(position, "y"))
                return ("COURSE_MAP_POSITION_INVALID", "Координаты узла должны быть конечными числами.");
            adjacency[id] = new List<string>();
        }

        if (!hasRootCourseNode)
            return ("COURSE_MAP_ROOT_REQUIRED", "Карта должна содержать узел корневого курса.");

        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var logicalEdges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in edgesElement.EnumerateArray())
        {
            if (edge.ValueKind != JsonValueKind.Object) return ("COURSE_MAP_EDGE_INVALID", "Каждая связь карты должна быть объектом.");
            var id = edge.TryGetProperty("id", out var idElement) ? idElement.GetString()?.Trim() : null;
            var source = edge.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString()?.Trim() : null;
            var target = edge.TryGetProperty("target", out var targetElement) ? targetElement.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(id) || id.Length > 180 || !edgeIds.Add(id)) return ("COURSE_MAP_EDGE_ID_INVALID", "Связи карты должны иметь уникальные непустые id.");
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) || !nodeIds.Contains(source) || !nodeIds.Contains(target))
                return ("COURSE_MAP_EDGE_ENDPOINT_INVALID", "Связь ссылается на отсутствующий узел.");
            if (source == target) return ("COURSE_MAP_SELF_EDGE", "Узел нельзя соединить сам с собой.");
            var sourceHandle = edge.TryGetProperty("sourceHandle", out var sourceHandleElement) ? sourceHandleElement.GetString()?.Trim() : "out";
            var targetHandle = edge.TryGetProperty("targetHandle", out var targetHandleElement) ? targetHandleElement.GetString()?.Trim() : "in";
            if (!string.Equals(sourceHandle, "out", StringComparison.Ordinal) || !string.Equals(targetHandle, "in", StringComparison.Ordinal))
                return ("COURSE_MAP_HANDLE_INVALID", "Связи карты должны идти из handle 'out' в handle 'in'.");
            if (!logicalEdges.Add($"{source}\u001f{target}"))
                return ("COURSE_MAP_EDGE_DUPLICATE", "Между двумя узлами уже существует такая связь.");
            adjacency[source].Add(target);
        }

        if (HasDirectedCycle(adjacency)) return ("COURSE_MAP_CYCLE", "Связи карты не должны образовывать цикл.");
        return null;
    }

    private static bool TryFiniteCoordinate(JsonElement position, string property)
    {
        if (!position.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)) return false;
        return double.IsFinite(number) && System.Math.Abs(number) <= 10_000_000;
    }

    private static bool HasDirectedCycle(Dictionary<string, List<string>> adjacency)
    {
        var inDegree = adjacency.Keys.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
        foreach (var targets in adjacency.Values)
        {
            foreach (var target in targets)
            {
                if (inDegree.ContainsKey(target)) inDegree[target]++;
            }
        }

        var pending = new Queue<string>(inDegree.Where(x => x.Value == 0).Select(x => x.Key));
        var visited = 0;
        while (pending.Count > 0)
        {
            var node = pending.Dequeue();
            visited++;
            if (!adjacency.TryGetValue(node, out var next)) continue;
            foreach (var child in next)
            {
                inDegree[child]--;
                if (inDegree[child] == 0) pending.Enqueue(child);
            }
        }

        return visited != adjacency.Count;
    }

    private static JsonElement? ParseDocumentElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}
