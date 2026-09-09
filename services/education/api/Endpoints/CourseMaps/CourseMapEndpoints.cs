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
        "math",
        "sql-test"
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
            TaskForgeDebugTrace.Map("EDITOR_MAP_GET_BEGIN", ("requestedCourse", courseId));
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var requested = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == courseId, ct);
            if (requested is null) return Microsoft.AspNetCore.Http.Results.NotFound();

            var root = await ResolveRootCourseAsync(courseId, db, ct);
            if (root is null || !CanEditCourse(access, root)) return Microsoft.AspNetCore.Http.Results.NotFound();

            var map = await db.CourseMaps.AsNoTracking().FirstOrDefaultAsync(x => x.RootCourseId == root.Id, ct);
            if (map is null)
            {
                TaskForgeDebugTrace.Map("EDITOR_MAP_GET_END",
                    ("user", access.UserId),
                    ("requestedCourse", courseId),
                    ("rootCourse", root.Id),
                    ("version", 0),
                    ("hasStoredMap", false));
                return Microsoft.AspNetCore.Http.Results.Ok(new CourseMapResponse(root.Id, courseId, 0, null, null, null));
            }

            var editorDocument = ParseDocumentElement(map.DocumentJson);
            var editorNodeCount = editorDocument.HasValue && editorDocument.Value.ValueKind == JsonValueKind.Object && editorDocument.Value.TryGetProperty("nodes", out var editorNodes) && editorNodes.ValueKind == JsonValueKind.Array ? editorNodes.GetArrayLength() : 0;
            var editorEdgeCount = editorDocument.HasValue && editorDocument.Value.ValueKind == JsonValueKind.Object && editorDocument.Value.TryGetProperty("edges", out var editorEdges) && editorEdges.ValueKind == JsonValueKind.Array ? editorEdges.GetArrayLength() : 0;
            TaskForgeDebugTrace.Map("EDITOR_MAP_GET_END",
                ("user", access.UserId),
                ("requestedCourse", courseId),
                ("rootCourse", root.Id),
                ("version", map.Version),
                ("hasStoredMap", true),
                ("documentHash", TaskForgeDebugTrace.Fingerprint(map.DocumentJson)),
                ("nodeCount", editorNodeCount),
                ("edgeCount", editorEdgeCount),
                ("updatedAt", map.UpdatedAt),
                ("updatedBy", map.UpdatedBy));

            return Microsoft.AspNetCore.Http.Results.Ok(new CourseMapResponse(
                root.Id,
                courseId,
                map.Version,
                editorDocument,
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
            var incomingNodeCount = request.Document.ValueKind == JsonValueKind.Object && request.Document.TryGetProperty("nodes", out var incomingNodes) && incomingNodes.ValueKind == JsonValueKind.Array ? incomingNodes.GetArrayLength() : 0;
            var incomingEdgeCount = request.Document.ValueKind == JsonValueKind.Object && request.Document.TryGetProperty("edges", out var incomingEdges) && incomingEdges.ValueKind == JsonValueKind.Array ? incomingEdges.GetArrayLength() : 0;
            var incomingNodeIds = request.Document.ValueKind == JsonValueKind.Object && request.Document.TryGetProperty("nodes", out var saveNodes) && saveNodes.ValueKind == JsonValueKind.Array
                ? saveNodes.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x))
                : Enumerable.Empty<string?>();
            var incomingEdgeIds = request.Document.ValueKind == JsonValueKind.Object && request.Document.TryGetProperty("edges", out var saveEdges) && saveEdges.ValueKind == JsonValueKind.Array
                ? saveEdges.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x))
                : Enumerable.Empty<string?>();
            TaskForgeDebugTrace.Map("EDITOR_MAP_SAVE_BEGIN",
                ("requestedCourse", courseId),
                ("expectedVersion", request.ExpectedVersion),
                ("documentHash", TaskForgeDebugTrace.Fingerprint(request.Document.GetRawText())),
                ("nodeCount", incomingNodeCount),
                ("edgeCount", incomingEdgeCount),
                ("nodeIds", TaskForgeDebugTrace.MapList(incomingNodeIds)),
                ("edgeIds", TaskForgeDebugTrace.MapList(incomingEdgeIds)));
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
                TaskForgeDebugTrace.Map("EDITOR_MAP_SAVE_REJECT",
                    ("user", access.UserId),
                    ("requestedCourse", courseId),
                    ("rootCourse", root.Id),
                    ("reason", "validation"),
                    ("code", validation.Value.Code),
                    ("message", validation.Value.Message));
                return Microsoft.AspNetCore.Http.Results.Json(new { message = validation.Value.Message, code = validation.Value.Code }, statusCode: StatusCodes.Status400BadRequest);
            }

            var normalizedJson = JsonSerializer.Serialize(request.Document, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var now = DateTimeOffset.UtcNow;
            var updatedBy = access.UserId.Value;
            var existing = await db.CourseMaps.AsNoTracking().FirstOrDefaultAsync(x => x.RootCourseId == root.Id, ct);
            TaskForgeDebugTrace.Map("EDITOR_MAP_SAVE_STATE",
                ("user", updatedBy),
                ("requestedCourse", courseId),
                ("rootCourse", root.Id),
                ("expectedVersion", request.ExpectedVersion),
                ("storedVersion", existing?.Version ?? 0),
                ("storedDocumentHash", TaskForgeDebugTrace.Fingerprint(existing?.DocumentJson)));
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

            TaskForgeDebugTrace.Map("EDITOR_MAP_SAVE_COMMIT",
                ("user", updatedBy),
                ("requestedCourse", courseId),
                ("rootCourse", root.Id),
                ("previousVersion", existing?.Version ?? 0),
                ("newVersion", nextVersion),
                ("documentHash", TaskForgeDebugTrace.Fingerprint(normalizedJson)),
                ("nodeCount", incomingNodeCount),
                ("edgeCount", incomingEdgeCount));

            await hub.Clients.Group(CourseMapPresenceHub.Group(root.Id)).SendAsync("MapSaved", new
            {
                rootCourseId = root.Id,
                version = nextVersion,
                updatedAt = now,
                updatedBy
            }, ct);

            TaskForgeDebugTrace.Map("EDITOR_MAP_SAVE_END",
                ("user", updatedBy),
                ("requestedCourse", courseId),
                ("rootCourse", root.Id),
                ("version", nextVersion),
                ("updatedAt", now));

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
        var currentId = requestedCourseId;
        var seen = new HashSet<Guid>();
        while (seen.Add(currentId))
        {
            var current = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == currentId, ct);
            if (current is null) return null;
            if (!current.ParentCourseId.HasValue) return current;
            currentId = current.ParentCourseId.Value;
        }
        return null;
    }

    private static async Task<List<CourseTreeCourseDto>> LoadCourseSubtreeRowsAsync(Guid rootCourseId, EducationDbContext db, CancellationToken ct)
    {
        var root = await db.Courses.AsNoTracking()
            .Where(x => x.Id == rootCourseId)
            .Select(x => new CourseTreeCourseDto(x.Id, x.ParentCourseId, x.Title, x.Description, x.IsPublic && !x.IsHiddenFromStudents, x.IsHiddenFromStudents, x.Sort))
            .FirstOrDefaultAsync(ct);
        if (root is null) return new List<CourseTreeCourseDto>();

        var result = new List<CourseTreeCourseDto> { root };
        var seen = new HashSet<Guid> { root.Id };
        var frontier = new List<Guid> { root.Id };

        while (frontier.Count > 0)
        {
            var parentOrder = frontier.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
            var children = await db.Courses.AsNoTracking()
                .Where(x => x.ParentCourseId.HasValue && frontier.Contains(x.ParentCourseId.Value))
                .Select(x => new CourseTreeCourseDto(x.Id, x.ParentCourseId, x.Title, x.Description, x.IsPublic && !x.IsHiddenFromStudents, x.IsHiddenFromStudents, x.Sort))
                .ToListAsync(ct);

            var ordered = children
                .Where(x => seen.Add(x.Id))
                .OrderBy(x => x.ParentCourseId.HasValue && parentOrder.TryGetValue(x.ParentCourseId.Value, out var index) ? index : int.MaxValue)
                .ThenBy(x => x.Sort)
                .ThenBy(x => x.Title)
                .ToList();

            result.AddRange(ordered);
            frontier = ordered.Select(x => x.Id).ToList();
        }

        return result;
    }

    private static async Task<HashSet<Guid>> LoadCourseSubtreeIdsAsync(Guid rootCourseId, EducationDbContext db, CancellationToken ct)
        => (await LoadCourseSubtreeRowsAsync(rootCourseId, db, ct)).Select(x => x.Id).ToHashSet();

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
            if (string.Equals(kind, "course", StringComparison.OrdinalIgnoreCase) && !subtreeCourseIds.Contains(entityId))
                return ("COURSE_MAP_COURSE_OUTSIDE_TREE", "Карта содержит курс за пределами текущего дерева.");
            if (string.Equals(kind, "course", StringComparison.OrdinalIgnoreCase) && entityId == rootCourseId)
                hasRootCourseNode = true;
            if (node.TryGetProperty("settings", out var nodeSettings)
                && nodeSettings.ValueKind == JsonValueKind.Object
                && nodeSettings.TryGetProperty("synthetic", out var syntheticNode)
                && syntheticNode.ValueKind == JsonValueKind.True)
            {
                return ("COURSE_MAP_SYNTHETIC_FORBIDDEN", "В редакторскую карту нельзя сохранять временные узлы ученического режима.");
            }

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

            if (edge.TryGetProperty("settings", out var settingsElement) && settingsElement.ValueKind != JsonValueKind.Null)
            {
                if (settingsElement.ValueKind != JsonValueKind.Object)
                    return ("COURSE_MAP_EDGE_SETTINGS_INVALID", "Настройки связи должны быть объектом.");
                if (settingsElement.TryGetProperty("synthetic", out var syntheticEdge) && syntheticEdge.ValueKind == JsonValueKind.True)
                    return ("COURSE_MAP_SYNTHETIC_FORBIDDEN", "В редакторскую карту нельзя сохранять временные связи ученического режима.");
                if (settingsElement.TryGetProperty("accessMode", out var modeElement) && modeElement.ValueKind != JsonValueKind.Null)
                {
                    if (modeElement.ValueKind != JsonValueKind.String)
                        return ("COURSE_MAP_EDGE_ACCESS_MODE_INVALID", "Режим открытия ветки должен быть строкой.");
                    var mode = modeElement.GetString()?.Trim().ToLowerInvariant();
                    if (mode is not null and not "normal" and not "after-prerequisites" and not "sequential")
                        return ("COURSE_MAP_EDGE_ACCESS_MODE_INVALID", "Неизвестный режим открытия ветки.");
                }

                foreach (var flagName in new[] { "gateUntilPrerequisites", "sequentialReveal" })
                {
                    if (!settingsElement.TryGetProperty(flagName, out var flagElement) || flagElement.ValueKind == JsonValueKind.Null) continue;
                    if (flagElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                        return ("COURSE_MAP_EDGE_ACCESS_FLAG_INVALID", $"Настройка {flagName} должна быть логическим значением.");
                }

                foreach (var effectName in new[] { "hiddenEffect", "sequentialEffect" })
                {
                    if (!settingsElement.TryGetProperty(effectName, out var effectElement) || effectElement.ValueKind == JsonValueKind.Null) continue;
                    if (effectElement.ValueKind != JsonValueKind.String)
                        return ("COURSE_MAP_EDGE_EFFECT_INVALID", $"Настройка {effectName} должна быть строкой.");
                    var effect = effectElement.GetString()?.Trim().ToLowerInvariant();
                    if (effect is not "inherit" and not "start" and not "stop")
                        return ("COURSE_MAP_EDGE_EFFECT_INVALID", $"Неизвестное значение {effectName}: {effect ?? "(empty)"}.");
                }
            }

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
