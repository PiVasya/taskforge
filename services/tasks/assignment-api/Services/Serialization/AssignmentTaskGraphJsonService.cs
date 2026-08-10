using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Domain;

namespace TaskForge.Tasks.Api.Services.Serialization;

internal static class AssignmentTaskGraphJsonService
{
    internal const int SchemaVersion = 3;
    internal const string Format = "taskforge-task-graph";
    internal const string CourseReference = "$course";
    internal const int MaxTasks = 200;
    internal const int MaxConnections = 20_000;

    private static readonly HashSet<string> TopLevelFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "format", "tasks", "connections"
    };

    private static readonly HashSet<string> TaskFields = new(StringComparer.Ordinal)
    {
        "key", "id", "type", "title", "description", "language", "allowedLanguages", "tags",
        "difficulty", "rating", "starterCode", "testCases", "testSettings", "questions",
        "blocks", "codeForbiddenCalls", "codeRequiredCalls", "isVisible", "imageTestReferenceKey",
        "imageTestSimilarityThreshold"
    };

    private static readonly HashSet<string> ConnectionFields = new(StringComparer.Ordinal)
    {
        "from", "to", "access"
    };

    private static readonly HashSet<string> AccessFields = new(StringComparer.Ordinal)
    {
        "hidden", "sequential"
    };

    private static readonly HashSet<string> ForbiddenLayoutFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "nodes", "edges", "viewport", "position", "positionAbsolute", "x", "y", "coordinates",
        "layout", "positions", "mapPosition", "nodeId"
    };

    internal sealed record GraphTask(string Key, JsonElement Source);

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
        internal required IReadOnlyList<GraphTask> Tasks { get; init; }
        internal required IReadOnlyList<GraphConnection> Connections { get; init; }

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

            return new JsonObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["format"] = Format,
                ["tasks"] = tasks,
                ["connections"] = connections
            };
        }
    }

    private sealed record MapNode(string Id, string Type, Guid EntityId);
    private sealed record MapEdge(string Source, string Target, string HiddenEffect, string SequentialEffect);

    internal static bool LooksLikeCanonicalGraph(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String)
            return string.Equals(format.GetString()?.Trim(), Format, StringComparison.Ordinal);
        if (root.TryGetProperty("schemaVersion", out var schema)
            && schema.ValueKind == JsonValueKind.Number
            && schema.TryGetInt32(out var value))
            return value == SchemaVersion;
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

        ValidateNoLayoutFields(root, "$", issues);
        ValidateObjectFields(root, "$", TopLevelFields, issues, "Неизвестное поле верхнего уровня.");

        if (!root.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out var schemaValue)
            || schemaValue != SchemaVersion)
        {
            issues.Add(new ValidationIssue("$.schemaVersion", $"Ожидается значение {SchemaVersion}."));
        }

        if (!root.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.String
            || !string.Equals(format.GetString()?.Trim(), Format, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue("$.format", $"Ожидается значение \"{Format}\"."));
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

        if (tasksElement.GetArrayLength() == 0)
            issues.Add(new ValidationIssue("$.tasks", "Добавьте хотя бы одно задание."));
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

            ValidateNoLayoutFields(taskElement, path, issues);
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
                if (!keys.Add(key))
                    issues.Add(new ValidationIssue($"{path}.key", $"Ключ \"{key}\" используется повторно."));
            }

            if (taskElement.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null)
            {
                var rawId = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
                if (!Guid.TryParse(rawId, out var id) || id == Guid.Empty)
                    issues.Add(new ValidationIssue($"{path}.id", "id должен быть корректным GUID существующего задания."));
                else if (!ids.Add(id))
                    issues.Add(new ValidationIssue($"{path}.id", "Одно существующее задание нельзя объявлять дважды."));
            }

            tasks.Add(new GraphTask(key, taskElement.Clone()));
            taskIndex++;
        }

        var declaredKeys = tasks.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
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

            ValidateNoLayoutFields(connectionElement, path, issues);
            ValidateObjectFields(connectionElement, path, ConnectionFields, issues, "Неизвестное поле связи.");
            var from = ReadString(connectionElement, "from");
            var to = ReadString(connectionElement, "to");

            if (from.Length == 0)
                issues.Add(new ValidationIssue($"{path}.from", $"Укажите {CourseReference} или key задания."));
            else if (!string.Equals(from, CourseReference, StringComparison.Ordinal) && !declaredKeys.Contains(from))
                issues.Add(new ValidationIssue($"{path}.from", $"Задание \"{from}\" не объявлено в tasks."));

            if (to.Length == 0)
                issues.Add(new ValidationIssue($"{path}.to", "Укажите key следующего задания."));
            else if (string.Equals(to, CourseReference, StringComparison.Ordinal))
                issues.Add(new ValidationIssue($"{path}.to", $"{CourseReference} может быть только источником."));
            else if (!declaredKeys.Contains(to))
                issues.Add(new ValidationIssue($"{path}.to", $"Задание \"{to}\" не объявлено в tasks."));

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
                    ValidateNoLayoutFields(accessElement, $"{path}.access", issues);
                    ValidateObjectFields(accessElement, $"{path}.access", AccessFields, issues, "Неизвестное поле эффекта.");
                    hidden = ParseEffect(accessElement, "hidden", $"{path}.access.hidden", issues);
                    sequential = ParseEffect(accessElement, "sequential", $"{path}.access.sequential", issues);
                }
            }

            if (from.Length > 0 && to.Length > 0)
                connections.Add(new GraphConnection(from, to, hidden, sequential));
        }

        if (HasCycle(tasks, connections))
            issues.Add(new ValidationIssue("$.connections", "Связи заданий не должны образовывать цикл."));

        return issues.Count > 0
            ? (null, issues)
            : (new ParsedGraph { Tasks = tasks, Connections = connections }, issues);
    }

    internal static JsonObject BuildExport(Guid courseId, IReadOnlyList<Assignment> assignments, CourseMapInternalResponse map)
    {
        var ordered = assignments
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToList();
        var keyByAssignmentId = BuildTaskKeys(ordered);
        var orderByKey = ordered.Select((item, index) => (item, index))
            .ToDictionary(x => keyByAssignmentId[x.item.Id], x => x.index, StringComparer.Ordinal);

        var connections = new List<GraphConnection>();
        if (TryReadMap(map.Document, out var mapNodes, out var mapEdges))
        {
            var directIds = ordered.Select(x => x.Id).ToHashSet();
            var assignmentByNodeId = mapNodes
                .Where(x => !string.Equals(x.Type, "course", StringComparison.OrdinalIgnoreCase) && directIds.Contains(x.EntityId))
                .GroupBy(x => x.Id, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First().EntityId, StringComparer.Ordinal);
            var placedAssignmentIds = assignmentByNodeId.Values.ToHashSet();
            var courseNodeIds = mapNodes
                .Where(x => string.Equals(x.Type, "course", StringComparison.OrdinalIgnoreCase) && x.EntityId == courseId)
                .Select(x => x.Id)
                .ToHashSet(StringComparer.Ordinal);
            var includedIncomingAssignmentIds = new HashSet<Guid>();

            foreach (var edge in mapEdges)
            {
                if (!assignmentByNodeId.TryGetValue(edge.Target, out var targetAssignmentId)) continue;
                var targetKey = keyByAssignmentId[targetAssignmentId];
                if (courseNodeIds.Contains(edge.Source))
                {
                    AddConnection(connections, new GraphConnection(CourseReference, targetKey, edge.HiddenEffect, edge.SequentialEffect));
                    includedIncomingAssignmentIds.Add(targetAssignmentId);
                    continue;
                }
                if (!assignmentByNodeId.TryGetValue(edge.Source, out var sourceAssignmentId)) continue;
                if (sourceAssignmentId == targetAssignmentId) continue;
                AddConnection(connections, new GraphConnection(
                    keyByAssignmentId[sourceAssignmentId],
                    targetKey,
                    edge.HiddenEffect,
                    edge.SequentialEffect));
                includedIncomingAssignmentIds.Add(targetAssignmentId);
            }

            foreach (var assignmentId in placedAssignmentIds)
            {
                if (includedIncomingAssignmentIds.Contains(assignmentId)) continue;
                AddConnection(connections, new GraphConnection(CourseReference, keyByAssignmentId[assignmentId], "inherit", "inherit"));
            }
        }
        else if (ordered.Count > 0)
        {
            AddConnection(connections, new GraphConnection(CourseReference, keyByAssignmentId[ordered[0].Id], "inherit", "inherit"));
            for (var i = 0; i + 1 < ordered.Count; i++)
            {
                AddConnection(connections, new GraphConnection(
                    keyByAssignmentId[ordered[i].Id],
                    keyByAssignmentId[ordered[i + 1].Id],
                    "inherit",
                    "inherit"));
            }
        }

        connections.Sort((left, right) =>
        {
            var leftFrom = string.Equals(left.From, CourseReference, StringComparison.Ordinal) ? -1 : orderByKey[left.From];
            var rightFrom = string.Equals(right.From, CourseReference, StringComparison.Ordinal) ? -1 : orderByKey[right.From];
            var byFrom = leftFrom.CompareTo(rightFrom);
            if (byFrom != 0) return byFrom;
            var byTo = orderByKey[left.To].CompareTo(orderByKey[right.To]);
            if (byTo != 0) return byTo;
            var byHidden = string.CompareOrdinal(left.HiddenEffect, right.HiddenEffect);
            return byHidden != 0 ? byHidden : string.CompareOrdinal(left.SequentialEffect, right.SequentialEffect);
        });

        var tasksJson = new JsonArray();
        foreach (var assignment in ordered)
        {
            var task = new JsonObject { ["key"] = keyByAssignmentId[assignment.Id] };
            var dto = AssignmentApiSerializationService.ToImportDto(assignment);
            foreach (var property in dto)
                task[property.Key] = property.Value?.DeepClone();
            tasksJson.Add(task);
        }

        var connectionsJson = new JsonArray();
        foreach (var connection in connections)
            connectionsJson.Add(connection.ToJson());

        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["format"] = Format,
            ["tasks"] = tasksJson,
            ["connections"] = connectionsJson
        };
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
            nodes.Add(new MapNode(id, type, entityId));
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

    private static void ValidateNoLayoutFields(JsonElement value, string path, ICollection<ValidationIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var property in value.EnumerateObject())
        {
            if (ForbiddenLayoutFields.Contains(property.Name))
                issues.Add(new ValidationIssue($"{path}.{property.Name}", "Расположение нод задаёт TaskForge; это поле нельзя импортировать."));
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
            if (ForbiddenLayoutFields.Contains(property.Name)) continue;
            if (!allowed.Contains(property.Name))
                issues.Add(new ValidationIssue($"{path}.{property.Name}", unknownMessage));
        }
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

    private static bool HasCycle(IReadOnlyList<GraphTask> tasks, IReadOnlyList<GraphConnection> connections)
    {
        var adjacency = tasks.ToDictionary(x => x.Key, _ => new List<string>(), StringComparer.Ordinal);
        var inDegree = tasks.ToDictionary(x => x.Key, _ => 0, StringComparer.Ordinal);
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
