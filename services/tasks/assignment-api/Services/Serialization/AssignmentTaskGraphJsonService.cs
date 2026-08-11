using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Domain;

namespace TaskForge.Tasks.Api.Services.Serialization;

internal static class AssignmentTaskGraphJsonService
{
    internal const int SchemaVersion = 4;
    internal const int LegacySchemaVersion = 3;
    internal const string Format = "taskforge-task-graph";
    internal const string CourseReference = "$course";
    internal const int MaxTasks = 5000;
    internal const int MaxConnections = 20_000;

    private static readonly HashSet<string> TopLevelFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "format", "scopes", "courses", "tasks", "connections", "layout"
    };

    private static readonly HashSet<string> TaskFields = new(StringComparer.Ordinal)
    {
        "key", "id", "course", "type", "title", "description", "language", "allowedLanguages", "tags",
        "difficulty", "rating", "starterCode", "testCases", "testSettings", "questions",
        "blocks", "codeForbiddenCalls", "codeRequiredCalls", "isVisible", "imageTestReferenceKey",
        "imageTestSimilarityThreshold"
    };

    private static readonly HashSet<string> CourseFields = new(StringComparer.Ordinal)
    {
        "key", "id", "title"
    };

    private static readonly HashSet<string> ConnectionFields = new(StringComparer.Ordinal)
    {
        "from", "to", "access"
    };

    private static readonly HashSet<string> AccessFields = new(StringComparer.Ordinal)
    {
        "hidden", "sequential"
    };

    private static readonly HashSet<string> ContentExportFields = new(StringComparer.Ordinal)
    {
        "type", "title", "description", "language", "allowedLanguages", "tags", "difficulty", "rating", "starterCode"
    };

    private static readonly HashSet<string> CheckExportFields = new(StringComparer.Ordinal)
    {
        "testCases", "testSettings", "questions", "blocks", "codeForbiddenCalls", "codeRequiredCalls",
        "imageTestReferenceKey", "imageTestSimilarityThreshold"
    };

    private static readonly HashSet<string> ForbiddenLegacyLayoutFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "nodes", "edges", "position", "positionAbsolute", "x", "y", "coordinates", "mapPosition", "nodeId"
    };

    private static readonly HashSet<string> LayoutFields = new(StringComparer.Ordinal)
    {
        "viewport", "positions"
    };

    private static readonly HashSet<string> PositionFields = new(StringComparer.Ordinal)
    {
        "x", "y"
    };

    private static readonly HashSet<string> ViewportFields = new(StringComparer.Ordinal)
    {
        "x", "y", "zoom"
    };

    internal static readonly string[] KnownScopes =
    {
        "ids", "content", "checks", "visibility", "connections", "connectionAccess", "layout"
    };

    internal sealed record GraphCourse(string Key, Guid Id, string Title);

    internal sealed record GraphTask(string Key, string CourseRef, JsonElement Source);

    internal sealed record GraphExportOptions(
        bool IncludeIds = true,
        bool IncludeContent = true,
        bool IncludeChecks = true,
        bool IncludeVisibility = true,
        bool IncludeConnections = true,
        bool IncludeConnectionAccess = true,
        bool IncludeLayout = true);

    internal sealed record GraphImportOptions(
        bool UpdateContent = true,
        bool UpdateChecks = true,
        bool UpdateVisibility = true,
        bool UpdateConnections = true,
        bool UpdateConnectionAccess = true,
        bool UpdateLayout = true);

    internal sealed record GraphConnection(string From, string To, string HiddenEffect, string SequentialEffect)
    {
        internal JsonObject ToJson()
        {
            var result = new JsonObject
            {
                ["from"] = From,
                ["to"] = To
            };
            if (HiddenEffect == "inherit" && SequentialEffect == "inherit") return result;

            var access = new JsonObject();
            if (HiddenEffect != "inherit") access["hidden"] = HiddenEffect;
            if (SequentialEffect != "inherit") access["sequential"] = SequentialEffect;
            result["access"] = access;
            return result;
        }
    }

    internal sealed record ValidationIssue(string Path, string Message);

    internal sealed class ParsedGraph
    {
        internal required IReadOnlyList<GraphCourse> Courses { get; init; }
        internal required IReadOnlyList<GraphTask> Tasks { get; init; }
        internal required IReadOnlyList<GraphConnection> Connections { get; init; }
        internal required IReadOnlySet<string> Scopes { get; init; }
        internal JsonElement? Layout { get; init; }
        internal int SourceSchemaVersion { get; init; }

        internal JsonObject ToTopologyJson()
        {
            var tasks = new JsonArray();
            foreach (var task in Tasks)
            {
                tasks.Add(new JsonObject { ["key"] = task.Key });
            }

            var connections = new JsonArray();
            foreach (var connection in Connections)
            {
                connections.Add(connection.ToJson());
            }

            var courses = new JsonArray();
            foreach (var course in Courses)
            {
                courses.Add(new JsonObject
                {
                    ["key"] = course.Key,
                    ["id"] = course.Id.ToString("D"),
                    ["title"] = course.Title
                });
            }

            return new JsonObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["format"] = Format,
                ["scopes"] = BuildScopesJson(Scopes),
                ["courses"] = courses,
                ["tasks"] = tasks,
                ["connections"] = connections
            };
        }
    }

    private sealed record MapNode(string Id, string Type, Guid EntityId, double X, double Y);
    private sealed record MapEdge(string Source, string Target, string HiddenEffect, string SequentialEffect);

    internal static bool LooksLikeCanonicalGraph(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String)
            return string.Equals(format.GetString()?.Trim(), Format, StringComparison.Ordinal);
        if (root.TryGetProperty("schemaVersion", out var schema)
            && schema.ValueKind == JsonValueKind.Number
            && schema.TryGetInt32(out var value))
            return value is SchemaVersion or LegacySchemaVersion;
        return root.TryGetProperty("tasks", out _) && root.TryGetProperty("connections", out _);
    }

    internal static (ParsedGraph? Graph, List<ValidationIssue> Issues) ParseAndValidate(JsonElement root)
    {
        var issues = new List<ValidationIssue>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ValidationIssue("$", "Ожидался объект графа заданий."));
            return (null, issues);
        }

        ValidateNoLegacyLayoutFields(root, "$", issues);
        ValidateObjectFields(root, "$", TopLevelFields, issues, "Неизвестное поле верхнего уровня.");

        var schemaValue = 0;
        if (!root.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out schemaValue)
            || (schemaValue != SchemaVersion && schemaValue != LegacySchemaVersion))
        {
            issues.Add(new ValidationIssue("$.schemaVersion", $"Поддерживаются версии {LegacySchemaVersion} и {SchemaVersion}."));
        }

        var scopes = ReadScopes(root, schemaValue, issues);
        var layout = ReadLayout(root, schemaValue, issues);
        if (layout.HasValue && schemaValue == SchemaVersion && !scopes.Contains("layout"))
            issues.Add(new ValidationIssue("$.scopes", "Добавьте scope layout, если документ содержит layout."));

        if (!root.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.String
            || !string.Equals(format.GetString()?.Trim(), Format, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue("$.format", $"Ожидается значение \"{Format}\"."));
        }

        var courses = new List<GraphCourse>();
        var courseRefs = new HashSet<string>(StringComparer.Ordinal) { CourseReference };
        var courseIds = new HashSet<Guid>();
        if (root.TryGetProperty("courses", out var coursesElement) && coursesElement.ValueKind != JsonValueKind.Null)
        {
            if (coursesElement.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new ValidationIssue("$.courses", "courses должен быть массивом существующих вложенных курсов."));
            }
            else
            {
                var courseIndex = 0;
                foreach (var courseElement in coursesElement.EnumerateArray())
                {
                    var path = $"$.courses[{courseIndex}]";
                    courseIndex++;
                    if (courseElement.ValueKind != JsonValueKind.Object)
                    {
                        issues.Add(new ValidationIssue(path, "Ожидался объект курса."));
                        continue;
                    }
                    ValidateNoLegacyLayoutFields(courseElement, path, issues);
                    ValidateObjectFields(courseElement, path, CourseFields, issues, "Неизвестное поле курса.");
                    var key = ReadString(courseElement, "key");
                    var rawId = ReadString(courseElement, "id");
                    var title = ReadString(courseElement, "title");
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        issues.Add(new ValidationIssue($"{path}.key", "Укажите уникальный key вложенного курса."));
                        key = $"invalid-course-{courseIndex}";
                    }
                    else
                    {
                        if (string.Equals(key, CourseReference, StringComparison.Ordinal))
                            issues.Add(new ValidationIssue($"{path}.key", $"{CourseReference} зарезервирован для текущего курса."));
                        if (key.Length > 80 || !key.All(IsKeyCharacter))
                            issues.Add(new ValidationIssue($"{path}.key", "Ключ должен содержать не больше 80 букв, цифр и символов . _ -."));
                        if (!courseRefs.Add(key))
                            issues.Add(new ValidationIssue($"{path}.key", $"Ключ \"{key}\" используется повторно."));
                    }
                    if (!Guid.TryParse(rawId, out var id) || id == Guid.Empty)
                    {
                        issues.Add(new ValidationIssue($"{path}.id", "id должен быть GUID существующего вложенного курса."));
                        id = Guid.Empty;
                    }
                    else if (!courseIds.Add(id))
                    {
                        issues.Add(new ValidationIssue($"{path}.id", "Один вложенный курс нельзя объявлять дважды."));
                    }
                    courses.Add(new GraphCourse(key, id, title));
                }
            }
        }

        if (!root.TryGetProperty("tasks", out var tasksElement) || tasksElement.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new ValidationIssue("$.tasks", "Нужен массив заданий."));
            return (null, issues);
        }

        if (!root.TryGetProperty("connections", out var connectionsElement) || connectionsElement.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new ValidationIssue("$.connections", "Нужен массив связей."));
            return (null, issues);
        }

        if (tasksElement.GetArrayLength() > MaxTasks)
            issues.Add(new ValidationIssue("$.tasks", $"За один импорт можно обработать не больше {MaxTasks} заданий."));
        if (connectionsElement.GetArrayLength() > MaxConnections)
            issues.Add(new ValidationIssue("$.connections", $"В графе не может быть больше {MaxConnections} связей."));

        var tasks = new List<GraphTask>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<Guid>();
        var taskIndex = 0;
        foreach (var taskElement in tasksElement.EnumerateArray())
        {
            var path = $"$.tasks[{taskIndex}]";
            if (taskElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new ValidationIssue(path, "Ожидался объект задания."));
                taskIndex++;
                continue;
            }

            ValidateNoLegacyLayoutFields(taskElement, path, issues);
            ValidateObjectFields(taskElement, path, TaskFields, issues, "Неизвестное поле задания.");

            var key = ReadString(taskElement, "key");
            if (string.IsNullOrWhiteSpace(key))
            {
                issues.Add(new ValidationIssue($"{path}.key", "Укажите уникальный ключ задания."));
                key = $"invalid-{taskIndex + 1}";
            }
            else
            {
                if (string.Equals(key, CourseReference, StringComparison.Ordinal))
                    issues.Add(new ValidationIssue($"{path}.key", $"{CourseReference} зарезервирован для текущего курса."));
                if (key.Length > 80 || !key.All(IsKeyCharacter))
                    issues.Add(new ValidationIssue($"{path}.key", "Ключ должен содержать не больше 80 букв, цифр и символов . _ -."));
                if (courseRefs.Contains(key) || !keys.Add(key))
                    issues.Add(new ValidationIssue($"{path}.key", $"Ключ \"{key}\" уже используется другим элементом графа."));
            }

            var courseRef = ReadString(taskElement, "course");
            if (courseRef.Length == 0) courseRef = CourseReference;
            if (!courseRefs.Contains(courseRef))
                issues.Add(new ValidationIssue($"{path}.course", $"Курс \"{courseRef}\" не объявлен в courses."));

            if (taskElement.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null)
            {
                var rawId = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
                if (!Guid.TryParse(rawId, out var id) || id == Guid.Empty)
                    issues.Add(new ValidationIssue($"{path}.id", "id должен быть корректным GUID существующего задания."));
                else if (!ids.Add(id))
                    issues.Add(new ValidationIssue($"{path}.id", "Одно существующее задание нельзя объявлять дважды."));
            }

            tasks.Add(new GraphTask(key, courseRef, taskElement.Clone()));
            taskIndex++;
        }

        var declaredKeys = tasks.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var declaredRefs = new HashSet<string>(courseRefs, StringComparer.Ordinal);
        declaredRefs.UnionWith(declaredKeys);
        if (layout.HasValue
            && layout.Value.ValueKind == JsonValueKind.Object
            && layout.Value.TryGetProperty("positions", out var positions)
            && positions.ValueKind == JsonValueKind.Object)
        {
            foreach (var position in positions.EnumerateObject())
            {
                if (!declaredRefs.Contains(position.Name))
                    issues.Add(new ValidationIssue($"$.layout.positions.{position.Name}", $"Элемент \"{position.Name}\" не объявлен в courses/tasks."));
            }
        }
        var connections = new List<GraphConnection>();
        var connectionKeys = new HashSet<string>(StringComparer.Ordinal);
        var connectionIndex = 0;
        foreach (var connectionElement in connectionsElement.EnumerateArray())
        {
            var path = $"$.connections[{connectionIndex}]";
            connectionIndex++;
            if (connectionElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new ValidationIssue(path, "Ожидался объект связи."));
                continue;
            }

            ValidateNoLegacyLayoutFields(connectionElement, path, issues);
            ValidateObjectFields(connectionElement, path, ConnectionFields, issues, "Неизвестное поле связи.");
            var from = ReadString(connectionElement, "from");
            var to = ReadString(connectionElement, "to");

            if (from.Length == 0)
                issues.Add(new ValidationIssue($"{path}.from", $"Укажите {CourseReference} или key задания."));
            else if (!declaredRefs.Contains(from))
                issues.Add(new ValidationIssue($"{path}.from", $"Элемент \"{from}\" не объявлен в courses/tasks."));

            if (to.Length == 0)
                issues.Add(new ValidationIssue($"{path}.to", "Укажите key следующего задания."));
            else if (string.Equals(to, CourseReference, StringComparison.Ordinal))
                issues.Add(new ValidationIssue($"{path}.to", $"{CourseReference} может быть только источником."));
            else if (!declaredRefs.Contains(to))
                issues.Add(new ValidationIssue($"{path}.to", $"Элемент \"{to}\" не объявлен в courses/tasks."));

            if (from.Length > 0 && string.Equals(from, to, StringComparison.Ordinal))
                issues.Add(new ValidationIssue(path, "Задание нельзя соединить с самим собой."));

            var signature = $"{from}\u001f{to}";
            if (from.Length > 0 && to.Length > 0 && !connectionKeys.Add(signature))
                issues.Add(new ValidationIssue(path, "Такая связь уже объявлена."));

            var hidden = "inherit";
            var sequential = "inherit";
            if (connectionElement.TryGetProperty("access", out var accessElement) && accessElement.ValueKind != JsonValueKind.Null)
            {
                if (accessElement.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new ValidationIssue($"{path}.access", "access должен быть объектом."));
                }
                else
                {
                    ValidateNoLegacyLayoutFields(accessElement, $"{path}.access", issues);
                    ValidateObjectFields(accessElement, $"{path}.access", AccessFields, issues, "Неизвестное поле эффекта.");
                    hidden = ParseEffect(accessElement, "hidden", $"{path}.access.hidden", issues);
                    sequential = ParseEffect(accessElement, "sequential", $"{path}.access.sequential", issues);
                }
            }

            if (from.Length > 0 && to.Length > 0)
                connections.Add(new GraphConnection(from, to, hidden, sequential));
        }

        if (HasCycle(courses, tasks, connections))
            issues.Add(new ValidationIssue("$.connections", "Связи заданий не должны образовывать цикл."));

        return issues.Count > 0
            ? (null, issues)
            : (new ParsedGraph
            {
                Courses = courses,
                Tasks = tasks,
                Connections = connections,
                Scopes = scopes,
                Layout = layout,
                SourceSchemaVersion = schemaValue
            }, issues);
    }

    internal static JsonObject BuildExport(
        Guid courseId,
        IReadOnlyList<Assignment> assignments,
        CourseTreeResponse tree,
        CourseMapInternalResponse map,
        GraphExportOptions options)
    {
        var subtreeCourseIds = tree.CourseIds.Where(x => x != Guid.Empty).ToHashSet();
        subtreeCourseIds.Add(courseId);
        var courseRows = tree.Courses
            .Where(x => subtreeCourseIds.Contains(x.Id))
            .OrderBy(x => x.ParentCourseId.HasValue)
            .ThenBy(x => x.Sort)
            .ThenBy(x => x.Title)
            .ThenBy(x => x.Id)
            .ToList();
        var ordered = assignments
            .Where(x => subtreeCourseIds.Contains(x.CourseId))
            .OrderBy(x => courseRows.FindIndex(c => c.Id == x.CourseId))
            .ThenBy(x => x.Sort)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToList();
        var keyByAssignmentId = BuildTaskKeys(ordered);
        var usedKeys = keyByAssignmentId.Values.ToHashSet(StringComparer.Ordinal);
        var keyByCourseId = BuildCourseKeys(courseRows.Where(x => x.Id != courseId).ToList(), usedKeys);
        string CourseRef(Guid id) => id == courseId ? CourseReference : keyByCourseId.GetValueOrDefault(id, string.Empty);

        var scopes = new List<string>();
        if (options.IncludeIds) scopes.Add("ids");
        if (options.IncludeContent) scopes.Add("content");
        if (options.IncludeChecks) scopes.Add("checks");
        if (options.IncludeVisibility) scopes.Add("visibility");
        if (options.IncludeConnections) scopes.Add("connections");
        if (options.IncludeConnectionAccess) scopes.Add("connectionAccess");
        if (options.IncludeLayout) scopes.Add("layout");

        List<MapNode> mapNodes = new();
        List<MapEdge> mapEdges = new();
        var hasMap = TryReadMap(map.Document, out mapNodes, out mapEdges);
        var referenceByNodeId = new Dictionary<string, string>(StringComparer.Ordinal);
        if (hasMap)
        {
            foreach (var node in mapNodes)
            {
                string? reference = null;
                if (string.Equals(node.Type, "course", StringComparison.OrdinalIgnoreCase))
                {
                    var courseRef = CourseRef(node.EntityId);
                    if (courseRef.Length > 0) reference = courseRef;
                }
                else if (keyByAssignmentId.TryGetValue(node.EntityId, out var taskKey))
                {
                    reference = taskKey;
                }
                if (reference != null) referenceByNodeId[node.Id] = reference;
            }
        }

        var connections = new List<GraphConnection>();
        if (hasMap && (options.IncludeConnections || options.IncludeConnectionAccess))
        {
            foreach (var edge in mapEdges)
            {
                if (!referenceByNodeId.TryGetValue(edge.Source, out var from)) continue;
                if (!referenceByNodeId.TryGetValue(edge.Target, out var to)) continue;
                if (string.Equals(from, to, StringComparison.Ordinal)) continue;
                AddConnection(connections, new GraphConnection(
                    from,
                    to,
                    options.IncludeConnectionAccess ? edge.HiddenEffect : "inherit",
                    options.IncludeConnectionAccess ? edge.SequentialEffect : "inherit"));
            }
        }
        else if (!hasMap && options.IncludeConnections)
        {
            var assignmentsByCourse = ordered.GroupBy(x => x.CourseId).ToDictionary(g => g.Key, g => g.ToList());
            var childrenByCourse = courseRows
                .Where(x => x.ParentCourseId.HasValue && subtreeCourseIds.Contains(x.ParentCourseId.Value))
                .GroupBy(x => x.ParentCourseId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Sort).ThenBy(x => x.Title).ToList());
            foreach (var course in courseRows.Where(x => subtreeCourseIds.Contains(x.Id)))
            {
                var source = CourseRef(course.Id);
                if (source.Length == 0) continue;
                var directAssignments = assignmentsByCourse.GetValueOrDefault(course.Id) ?? new List<Assignment>();
                if (directAssignments.Count > 0)
                {
                    AddConnection(connections, new GraphConnection(source, keyByAssignmentId[directAssignments[0].Id], "inherit", "inherit"));
                    for (var i = 0; i + 1 < directAssignments.Count; i++)
                    {
                        AddConnection(connections, new GraphConnection(
                            keyByAssignmentId[directAssignments[i].Id],
                            keyByAssignmentId[directAssignments[i + 1].Id],
                            "inherit",
                            "inherit"));
                    }
                }
                foreach (var child in childrenByCourse.GetValueOrDefault(course.Id) ?? new List<CourseTreeCourseDto>())
                {
                    var childRef = CourseRef(child.Id);
                    if (childRef.Length > 0) AddConnection(connections, new GraphConnection(source, childRef, "inherit", "inherit"));
                }
            }
        }

        var orderByRef = new Dictionary<string, int>(StringComparer.Ordinal) { [CourseReference] = -1 };
        var ordinal = 0;
        foreach (var course in courseRows.Where(x => x.Id != courseId)) orderByRef[keyByCourseId[course.Id]] = ordinal++;
        foreach (var assignment in ordered) orderByRef[keyByAssignmentId[assignment.Id]] = ordinal++;
        connections.Sort((left, right) =>
        {
            var byFrom = orderByRef.GetValueOrDefault(left.From, int.MaxValue).CompareTo(orderByRef.GetValueOrDefault(right.From, int.MaxValue));
            if (byFrom != 0) return byFrom;
            var byTo = orderByRef.GetValueOrDefault(left.To, int.MaxValue).CompareTo(orderByRef.GetValueOrDefault(right.To, int.MaxValue));
            if (byTo != 0) return byTo;
            var byHidden = string.CompareOrdinal(left.HiddenEffect, right.HiddenEffect);
            return byHidden != 0 ? byHidden : string.CompareOrdinal(left.SequentialEffect, right.SequentialEffect);
        });

        var coursesJson = new JsonArray();
        foreach (var course in courseRows.Where(x => x.Id != courseId))
        {
            coursesJson.Add(new JsonObject
            {
                ["key"] = keyByCourseId[course.Id],
                ["id"] = course.Id.ToString("D"),
                ["title"] = course.Title
            });
        }

        var tasksJson = new JsonArray();
        foreach (var assignment in ordered)
        {
            var task = new JsonObject
            {
                ["key"] = keyByAssignmentId[assignment.Id],
                ["course"] = CourseRef(assignment.CourseId)
            };
            var dto = AssignmentApiSerializationService.ToImportDto(assignment);
            foreach (var property in dto)
            {
                if (property.Key == "id")
                {
                    if (options.IncludeIds) task[property.Key] = property.Value?.DeepClone();
                    continue;
                }
                if (ContentExportFields.Contains(property.Key) && options.IncludeContent)
                    task[property.Key] = property.Value?.DeepClone();
                else if (CheckExportFields.Contains(property.Key) && options.IncludeChecks)
                    task[property.Key] = property.Value?.DeepClone();
                else if (property.Key == "isVisible" && options.IncludeVisibility)
                    task[property.Key] = property.Value?.DeepClone();
            }
            tasksJson.Add(task);
        }

        var connectionsJson = new JsonArray();
        foreach (var connection in connections) connectionsJson.Add(connection.ToJson());

        var result = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["format"] = Format,
            ["scopes"] = BuildScopesJson(scopes),
            ["courses"] = coursesJson,
            ["tasks"] = tasksJson,
            ["connections"] = connectionsJson
        };

        if (options.IncludeLayout && hasMap)
        {
            var positions = new JsonObject();
            foreach (var node in mapNodes)
            {
                if (!referenceByNodeId.TryGetValue(node.Id, out var reference) || positions.ContainsKey(reference)) continue;
                positions[reference] = new JsonObject { ["x"] = node.X, ["y"] = node.Y };
            }
            var layout = new JsonObject { ["positions"] = positions };
            if (TryReadViewport(map.Document, out var viewport)) layout["viewport"] = viewport;
            result["layout"] = layout;
        }

        return result;
    }

    private static Dictionary<Guid, string> BuildCourseKeys(IReadOnlyList<CourseTreeCourseDto> courses, HashSet<string> used)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var course in courses)
        {
            var baseKey = $"course-{Slugify(course.Title)}";
            var key = baseKey;
            var suffix = 2;
            while (!used.Add(key)) key = $"{baseKey}-{suffix++}";
            result[course.Id] = key;
        }
        return result;
    }

    internal static JsonArray BuildScopesJson(IEnumerable<string> scopes)
    {
        var result = new JsonArray();
        foreach (var scope in scopes.OrderBy(x => Array.IndexOf(KnownScopes, x))) result.Add(scope);
        return result;
    }

    private static Dictionary<Guid, string> BuildTaskKeys(IReadOnlyList<Assignment> assignments)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<Guid, string>();
        foreach (var assignment in assignments)
        {
            var baseKey = Slugify(assignment.Title);
            var key = baseKey;
            var suffix = 2;
            while (!used.Add(key)) key = $"{baseKey}-{suffix++}";
            result[assignment.Id] = key;
        }
        return result;
    }

    private static string Slugify(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        var builder = new StringBuilder();
        var separatorPending = false;
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && builder.Length > 0) builder.Append('-');
                builder.Append(character);
                separatorPending = false;
            }
            else
            {
                separatorPending = true;
            }
            if (builder.Length >= 58) break;
        }
        var result = builder.ToString().Trim('-');
        return result.Length > 0 ? result : "task";
    }

    private static bool TryReadMap(JsonElement? document, out List<MapNode> nodes, out List<MapEdge> edges)
    {
        nodes = new List<MapNode>();
        edges = new List<MapEdge>();
        if (!document.HasValue || document.Value.ValueKind != JsonValueKind.Object) return false;
        var root = document.Value;
        if (!root.TryGetProperty("nodes", out var nodeArray) || nodeArray.ValueKind != JsonValueKind.Array) return false;
        if (!root.TryGetProperty("edges", out var edgeArray) || edgeArray.ValueKind != JsonValueKind.Array) return false;

        foreach (var node in nodeArray.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(node, "id");
            var type = ReadString(node, "type");
            var entityRaw = ReadString(node, "entityId");
            if (id.Length == 0 || type.Length == 0 || !Guid.TryParse(entityRaw, out var entityId) || entityId == Guid.Empty) continue;
            var x = 0d;
            var y = 0d;
            if (node.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Object)
            {
                x = ReadDouble(position, "x");
                y = ReadDouble(position, "y");
            }
            nodes.Add(new MapNode(id, type, entityId, x, y));
        }

        foreach (var edge in edgeArray.EnumerateArray())
        {
            if (edge.ValueKind != JsonValueKind.Object) continue;
            var source = ReadString(edge, "source");
            var target = ReadString(edge, "target");
            if (source.Length == 0 || target.Length == 0 || source == target) continue;
            var hidden = "inherit";
            var sequential = "inherit";
            if (edge.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
            {
                var legacyMode = ReadString(settings, "accessMode").ToLowerInvariant();
                hidden = NormalizeEffect(ReadString(settings, "hiddenEffect"),
                    ReadBool(settings, "gateUntilPrerequisites") || legacyMode == "after-prerequisites");
                sequential = NormalizeEffect(ReadString(settings, "sequentialEffect"),
                    ReadBool(settings, "sequentialReveal") || legacyMode == "sequential");
            }
            edges.Add(new MapEdge(source, target, hidden, sequential));
        }

        return true;
    }

    private static void ValidateNoLegacyLayoutFields(JsonElement value, string path, ICollection<ValidationIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var property in value.EnumerateObject())
        {
            if (ForbiddenLegacyLayoutFields.Contains(property.Name))
                issues.Add(new ValidationIssue($"{path}.{property.Name}", "Используйте layout.positions вместо координат внутри объектов."));
        }
    }

    private static void ValidateObjectFields(
        JsonElement value,
        string path,
        IReadOnlySet<string> allowed,
        ICollection<ValidationIssue> issues,
        string unknownMessage)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (ForbiddenLegacyLayoutFields.Contains(property.Name)) continue;
            if (!allowed.Contains(property.Name))
                issues.Add(new ValidationIssue($"{path}.{property.Name}", unknownMessage));
        }
    }

    private static IReadOnlySet<string> ReadScopes(JsonElement root, int schemaVersion, ICollection<ValidationIssue> issues)
    {
        if (schemaVersion == LegacySchemaVersion) return KnownScopes.Where(x => x != "layout").ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("scopes", out var scopes))
        {
            issues.Add(new ValidationIssue("$.scopes", "Укажите массив разделов, которые содержит JSON."));
            return result;
        }
        if (scopes.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new ValidationIssue("$.scopes", "scopes должен быть массивом строк."));
            return result;
        }
        foreach (var item in scopes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                issues.Add(new ValidationIssue("$.scopes", "Каждый scope должен быть строкой."));
                continue;
            }
            var value = item.GetString()?.Trim() ?? string.Empty;
            if (!KnownScopes.Contains(value, StringComparer.Ordinal))
            {
                issues.Add(new ValidationIssue("$.scopes", $"Неизвестный scope: {value}."));
                continue;
            }
            if (!result.Add(value)) issues.Add(new ValidationIssue("$.scopes", $"Scope {value} указан повторно."));
        }
        return result;
    }

    private static JsonElement? ReadLayout(JsonElement root, int schemaVersion, ICollection<ValidationIssue> issues)
    {
        if (!root.TryGetProperty("layout", out var layout) || layout.ValueKind == JsonValueKind.Null) return null;
        if (schemaVersion == LegacySchemaVersion)
        {
            issues.Add(new ValidationIssue("$.layout", "layout поддерживается начиная со schemaVersion 4."));
            return null;
        }
        if (layout.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ValidationIssue("$.layout", "layout должен быть объектом."));
            return null;
        }
        ValidateObjectFields(layout, "$.layout", LayoutFields, issues, "Неизвестное поле layout.");
        if (layout.TryGetProperty("viewport", out var viewport) && viewport.ValueKind != JsonValueKind.Null)
        {
            if (viewport.ValueKind != JsonValueKind.Object) issues.Add(new ValidationIssue("$.layout.viewport", "viewport должен быть объектом."));
            else
            {
                ValidateObjectFields(viewport, "$.layout.viewport", ViewportFields, issues, "Неизвестное поле viewport.");
                ValidateFiniteNumber(viewport, "x", "$.layout.viewport.x", issues);
                ValidateFiniteNumber(viewport, "y", "$.layout.viewport.y", issues);
                ValidateFiniteNumber(viewport, "zoom", "$.layout.viewport.zoom", issues, required: false, min: 0.05, max: 4);
            }
        }
        if (layout.TryGetProperty("positions", out var positions) && positions.ValueKind != JsonValueKind.Null)
        {
            if (positions.ValueKind != JsonValueKind.Object) issues.Add(new ValidationIssue("$.layout.positions", "positions должен быть объектом key -> {x,y}."));
            else
            {
                foreach (var property in positions.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        issues.Add(new ValidationIssue($"$.layout.positions.{property.Name}", "Позиция должна быть объектом {x,y}."));
                        continue;
                    }
                    ValidateObjectFields(property.Value, $"$.layout.positions.{property.Name}", PositionFields, issues, "Неизвестное поле позиции.");
                    ValidateFiniteNumber(property.Value, "x", $"$.layout.positions.{property.Name}.x", issues);
                    ValidateFiniteNumber(property.Value, "y", $"$.layout.positions.{property.Name}.y", issues);
                }
            }
        }
        return layout.Clone();
    }

    private static void ValidateFiniteNumber(
        JsonElement owner, string propertyName, string path, ICollection<ValidationIssue> issues,
        bool required = true, double? min = null, double? max = null)
    {
        if (!owner.TryGetProperty(propertyName, out var value))
        {
            if (required) issues.Add(new ValidationIssue(path, "Укажите число."));
            return;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number))
        {
            issues.Add(new ValidationIssue(path, "Ожидается конечное число."));
            return;
        }
        if (min.HasValue && number < min.Value || max.HasValue && number > max.Value)
            issues.Add(new ValidationIssue(path, $"Значение должно быть от {min ?? double.MinValue} до {max ?? double.MaxValue}."));
    }

    private static bool TryReadViewport(JsonElement? document, out JsonObject viewport)
    {
        viewport = new JsonObject();
        if (!document.HasValue || document.Value.ValueKind != JsonValueKind.Object) return false;
        if (!document.Value.TryGetProperty("viewport", out var source) || source.ValueKind != JsonValueKind.Object) return false;
        viewport["x"] = ReadDouble(source, "x");
        viewport["y"] = ReadDouble(source, "y");
        var zoom = ReadDouble(source, "zoom");
        viewport["zoom"] = zoom > 0 ? zoom : 1;
        return true;
    }

    private static double ReadDouble(JsonElement owner, string propertyName)
    {
        return owner.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : 0d;
    }

    private static string ParseEffect(JsonElement owner, string propertyName, string path, ICollection<ValidationIssue> issues)
    {
        if (!owner.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null) return "inherit";
        if (element.ValueKind != JsonValueKind.String)
        {
            issues.Add(new ValidationIssue(path, "Эффект должен быть строкой start, stop или inherit."));
            return "inherit";
        }
        var value = element.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value is "start" or "stop" or "inherit") return value;
        issues.Add(new ValidationIssue(path, "Допустимы только start, stop и inherit."));
        return "inherit";
    }

    private static bool HasCycle(IReadOnlyList<GraphCourse> courses, IReadOnlyList<GraphTask> tasks, IReadOnlyList<GraphConnection> connections)
    {
        var refs = courses.Select(x => x.Key).Concat(tasks.Select(x => x.Key)).Distinct(StringComparer.Ordinal).ToArray();
        var adjacency = refs.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);
        var inDegree = refs.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            if (string.Equals(connection.From, CourseReference, StringComparison.Ordinal)) continue;
            if (!adjacency.TryGetValue(connection.From, out var targets) || !inDegree.ContainsKey(connection.To)) continue;
            targets.Add(connection.To);
            inDegree[connection.To]++;
        }
        var queue = new Queue<string>(inDegree.Where(x => x.Value == 0).Select(x => x.Key));
        var visited = 0;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            visited++;
            foreach (var target in adjacency[current])
            {
                inDegree[target]--;
                if (inDegree[target] == 0) queue.Enqueue(target);
            }
        }
        return visited != adjacency.Count;
    }

    private static void AddConnection(List<GraphConnection> target, GraphConnection connection)
    {
        if (target.Any(x => string.Equals(x.From, connection.From, StringComparison.Ordinal)
            && string.Equals(x.To, connection.To, StringComparison.Ordinal))) return;
        target.Add(connection);
    }

    private static bool IsKeyCharacter(char value) => char.IsLetterOrDigit(value) || value is '-' or '_' or '.';

    private static string NormalizeEffect(string? value, bool legacyStart)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "start" or "stop" or "inherit") return normalized;
        return legacyStart ? "start" : "inherit";
    }

    private static string ReadString(JsonElement owner, string propertyName)
    {
        return owner.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static bool ReadBool(JsonElement owner, string propertyName)
    {
        return owner.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
    }
}
