using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;

namespace TaskForge.Tasks.Api.Services.Access;

internal static class CourseMapProgressionService
{
    internal const string AccessModeAfterPrerequisites = "after-prerequisites";
    internal const string AccessModeSequential = "sequential";

    private enum EffectTransition
    {
        Inherit,
        Start,
        Stop
    }

    internal sealed record Evaluation(
        Guid RootCourseId,
        Guid RequestedCourseId,
        int Version,
        HashSet<Guid> RequestedSubtreeCourseIds,
        HashSet<Guid> VisibleCourseIds,
        HashSet<Guid> VisibleAssignmentIds,
        HashSet<Guid> SolvedAssignmentIds,
        JsonElement? LearningDocument,
        DateTimeOffset? UpdatedAt,
        Guid? UpdatedBy);

    private sealed record MapNode(string Id, string Type, Guid? EntityId, double X, double Y, JsonElement? Settings);
    private sealed record MapEdge(string Id, string Source, string Target, EffectTransition HiddenEffect, EffectTransition SequentialEffect, JsonElement? Settings);
    private sealed record TraversalState(string NodeId, bool Hidden, bool Sequential);
    private sealed record BlockedSequentialEdge(MapEdge Edge, string SourceNodeId, string TargetNodeId);
    private sealed record AssignmentProgressionRow(Guid Id, Guid CourseId, string Title);

    internal static async Task<Evaluation?> LoadEvaluationAsync(
        Guid requestedCourseId,
        Guid userId,
        TasksDbContext db,
        IHttpClientFactory clients,
        IConfiguration cfg,
        CancellationToken ct)
    {
        if (requestedCourseId == Guid.Empty || userId == Guid.Empty) return null;

        // Always resolve the map first. User access is evaluated in one batch for the
        // complete root tree below, so a separate access request for the requested
        // course here would only duplicate an internal HTTP + DB round trip.
        // The map endpoint returns the real root even when
        // no map has been saved yet. Starting progression from requestedCourseId would
        // allow a direct URL to bypass a gate that lives higher in the course graph.
        var map = await GetInternalAsync<CourseMapInternalResponse>(
            clients,
            cfg,
            ServiceUrl(cfg, "EducationApi", "http://education-api:8080"),
            $"/api/internal/courses/{requestedCourseId:D}/map",
            ct);
        if (map == null || map.RootCourseId == Guid.Empty) return null;

        var tree = await GetInternalAsync<CourseTreeResponse>(
            clients,
            cfg,
            ServiceUrl(cfg, "EducationApi", "http://education-api:8080"),
            $"/api/internal/courses/{map.RootCourseId:D}/tree",
            ct);
        if (tree == null || tree.CourseIds.Length == 0 || tree.Courses.All(x => x.Id != requestedCourseId)) return null;

        var allTreeCourseIds = tree.CourseIds.Where(x => x != Guid.Empty).Distinct().Take(5000).ToArray();
        var accessibleCourseIds = await LoadAccessibleCourseIdsAsync(allTreeCourseIds, userId, clients, cfg, ct);
        if (!accessibleCourseIds.Contains(requestedCourseId)) return null;

        var requestedSubtreeCourseIds = BuildSubtreeCourseIds(requestedCourseId, tree.Courses);
        requestedSubtreeCourseIds.IntersectWith(accessibleCourseIds);

        var assignmentRows = await db.Assignments.AsNoTracking()
            .Where(x => accessibleCourseIds.Contains(x.CourseId) && x.IsVisible)
            .Select(x => new AssignmentProgressionRow(x.Id, x.CourseId, x.Title))
            .ToListAsync(ct);

        var solvedIds = await LoadSolvedAssignmentIdsAsync(userId, assignmentRows.Select(x => x.Id), db, clients, cfg, ct);

        if (map.Document is null || map.Document.Value.ValueKind != JsonValueKind.Object)
        {
            return new Evaluation(
                map.RootCourseId,
                requestedCourseId,
                map.Version,
                requestedSubtreeCourseIds,
                accessibleCourseIds,
                assignmentRows.Select(x => x.Id).ToHashSet(),
                solvedIds,
                null,
                map.UpdatedAt,
                map.UpdatedBy);
        }

        var evaluation = EvaluateDocument(
            map.RootCourseId,
            requestedCourseId,
            map.Version,
            map.Document.Value,
            tree,
            requestedSubtreeCourseIds,
            accessibleCourseIds,
            assignmentRows,
            solvedIds,
            map.UpdatedAt,
            map.UpdatedBy);

        // A course that sits behind an unopened graph branch should behave exactly as
        // absent for a learner. This also prevents direct child-course URLs from being
        // used to jump over an upstream after-prerequisites gate.
        return evaluation.VisibleCourseIds.Contains(requestedCourseId) ? evaluation : null;
    }

    private static Evaluation EvaluateDocument(
        Guid rootCourseId,
        Guid requestedCourseId,
        int version,
        JsonElement document,
        CourseTreeResponse tree,
        HashSet<Guid> requestedSubtreeCourseIds,
        HashSet<Guid> accessibleCourseIds,
        List<AssignmentProgressionRow> assignmentRows,
        HashSet<Guid> solvedIds,
        DateTimeOffset? updatedAt,
        Guid? updatedBy)
    {
        var assignmentById = assignmentRows.ToDictionary(x => x.Id);
        var courseById = tree.Courses.ToDictionary(x => x.Id);
        var nodes = ParseNodes(document);
        var nodeById = nodes.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var edges = ParseEdges(document, nodeById.Keys.ToHashSet(StringComparer.Ordinal));
        var edgeById = edges.ToDictionary(x => x.Id, StringComparer.Ordinal);

        bool IsNodeAllowed(MapNode node)
        {
            if (!node.EntityId.HasValue) return false;
            if (string.Equals(node.Type, "course", StringComparison.OrdinalIgnoreCase))
                return accessibleCourseIds.Contains(node.EntityId.Value) && courseById.ContainsKey(node.EntityId.Value);
            return assignmentById.ContainsKey(node.EntityId.Value);
        }

        var validNodeIds = nodes.Where(IsNodeAllowed).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        edges = edges.Where(x => validNodeIds.Contains(x.Source) && validNodeIds.Contains(x.Target)).ToList();
        edgeById = edges.ToDictionary(x => x.Id, StringComparer.Ordinal);

        var incoming = validNodeIds.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);
        var outgoing = validNodeIds.ToDictionary(x => x, _ => new List<MapEdge>(), StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            incoming[edge.Target].Add(edge.Source);
            outgoing[edge.Source].Add(edge);
        }

        bool SelfComplete(string nodeId)
        {
            if (!nodeById.TryGetValue(nodeId, out var node)) return false;
            if (string.Equals(node.Type, "course", StringComparison.OrdinalIgnoreCase)) return true;
            return node.EntityId.HasValue && solvedIds.Contains(node.EntityId.Value);
        }

        var throughComplete = ComputeThroughCompletion(validNodeIds, incoming, SelfComplete);
        var start = nodes.FirstOrDefault(x =>
            string.Equals(x.Type, "course", StringComparison.OrdinalIgnoreCase)
            && x.EntityId == rootCourseId
            && validNodeIds.Contains(x.Id));

        if (start is null)
        {
            return new Evaluation(
                rootCourseId,
                requestedCourseId,
                version,
                requestedSubtreeCourseIds,
                new HashSet<Guid>(),
                new HashSet<Guid>(),
                solvedIds,
                null,
                updatedAt,
                updatedBy);
        }

        var visibleNodeIds = new HashSet<string>(StringComparer.Ordinal);
        var visibleEdgeIds = new HashSet<string>(StringComparer.Ordinal);
        var blockedSequentialEdges = new List<BlockedSequentialEdge>();
        var states = new Queue<TraversalState>();
        var seenStates = new HashSet<string>(StringComparer.Ordinal);
        states.Enqueue(new TraversalState(start.Id, false, false));

        while (states.Count > 0)
        {
            var state = states.Dequeue();
            if (!validNodeIds.Contains(state.NodeId)) continue;

            // Only four logical states can reach one node: normal, hidden, sequential,
            // or both. This keeps traversal linear in the graph size with a small
            // constant even when many branches merge.
            var stateKey = $"{state.NodeId}\u001f{(state.Hidden ? 1 : 0)}\u001f{(state.Sequential ? 1 : 0)}";
            if (!seenStates.Add(stateKey)) continue;
            visibleNodeIds.Add(state.NodeId);

            if (!outgoing.TryGetValue(state.NodeId, out var nextEdges)) continue;

            // Full hiding is stronger than sequential reveal. While a hidden section is
            // active, every next transition requires the entire path reaching the current
            // node to be complete and no placeholder is emitted.
            var hiddenSourceLocked = state.Hidden && !throughComplete.GetValueOrDefault(state.NodeId);
            if (hiddenSourceLocked) continue;

            var sequentialSourceLocked = state.Sequential && !SelfComplete(state.NodeId);

            foreach (var edge in nextEdges)
            {
                // A hidden section begins only after every prerequisite on all incoming
                // paths to this exact point is complete. This is intentionally stricter
                // than sequential reveal, which only waits for the currently visible task.
                if (edge.HiddenEffect == EffectTransition.Start
                    && !throughComplete.GetValueOrDefault(state.NodeId))
                {
                    continue;
                }

                if (sequentialSourceLocked)
                {
                    // If this very edge starts full hiding, do not leak even a synthetic
                    // continuation marker. Otherwise show the safe lock placeholder.
                    if (edge.HiddenEffect != EffectTransition.Start)
                        blockedSequentialEdges.Add(new BlockedSequentialEdge(edge, state.NodeId, edge.Target));
                    continue;
                }

                visibleEdgeIds.Add(edge.Id);
                states.Enqueue(new TraversalState(
                    edge.Target,
                    ApplyEffect(state.Hidden, edge.HiddenEffect),
                    ApplyEffect(state.Sequential, edge.SequentialEffect)));
            }
        }

        var visibleAssignments = visibleNodeIds
            .Select(id => nodeById[id])
            .Where(node => !string.Equals(node.Type, "course", StringComparison.OrdinalIgnoreCase) && node.EntityId.HasValue)
            .Select(node => node.EntityId!.Value)
            .Where(assignmentById.ContainsKey)
            .ToHashSet();

        var visibleCourses = visibleNodeIds
            .Select(id => nodeById[id])
            .Where(node => string.Equals(node.Type, "course", StringComparison.OrdinalIgnoreCase) && node.EntityId.HasValue)
            .Select(node => node.EntityId!.Value)
            .Where(accessibleCourseIds.Contains)
            .ToHashSet();

        var outputNodes = new List<object>();
        if (document.TryGetProperty("nodes", out var sourceNodes) && sourceNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var nodeElement in sourceNodes.EnumerateArray())
            {
                var id = nodeElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || !visibleNodeIds.Contains(id)) continue;
                var parsed = nodeById[id];
                outputNodes.Add(new
                {
                    id = parsed.Id,
                    type = parsed.Type,
                    entityId = parsed.EntityId?.ToString("D"),
                    position = new { x = parsed.X, y = parsed.Y },
                    settings = parsed.Settings
                });
            }
        }

        var outputEdges = new List<object>();
        if (document.TryGetProperty("edges", out var sourceEdges) && sourceEdges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edgeElement in sourceEdges.EnumerateArray())
            {
                var id = edgeElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || !visibleEdgeIds.Contains(id) || !edgeById.TryGetValue(id, out var edge)) continue;
                if (!visibleNodeIds.Contains(edge.Source) || !visibleNodeIds.Contains(edge.Target)) continue;
                outputEdges.Add(new
                {
                    id = edge.Id,
                    source = edge.Source,
                    sourceHandle = "out",
                    target = edge.Target,
                    targetHandle = "in",
                    settings = edge.Settings
                });
            }
        }

        AppendSequentialLocks(
            blockedSequentialEdges,
            visibleNodeIds,
            nodeById,
            assignmentById,
            courseById,
            outputNodes,
            outputEdges);

        var viewport = document.TryGetProperty("viewport", out var viewportElement) && viewportElement.ValueKind == JsonValueKind.Object
            ? viewportElement.Clone()
            : JsonSerializer.SerializeToElement(new { x = 0, y = 0, zoom = 1 });
        var learningDocument = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            viewport,
            nodes = outputNodes,
            edges = outputEdges
        });

        return new Evaluation(
            rootCourseId,
            requestedCourseId,
            version,
            requestedSubtreeCourseIds,
            visibleCourses,
            visibleAssignments,
            solvedIds,
            learningDocument,
            updatedAt,
            updatedBy);
    }

    private static void AppendSequentialLocks(
        List<BlockedSequentialEdge> blockedEdges,
        HashSet<string> visibleNodeIds,
        Dictionary<string, MapNode> nodeById,
        Dictionary<Guid, AssignmentProgressionRow> assignments,
        Dictionary<Guid, CourseTreeCourseDto> courses,
        List<object> outputNodes,
        List<object> outputEdges)
    {
        // If another route already exposed the target, a lock would be misleading.
        var trulyBlocked = blockedEdges
            .Where(x => !visibleNodeIds.Contains(x.TargetNodeId))
            .Where(x => visibleNodeIds.Contains(x.SourceNodeId))
            .Where(x => nodeById.ContainsKey(x.TargetNodeId))
            .GroupBy(x => x.TargetNodeId, StringComparer.Ordinal)
            .ToList();

        foreach (var group in trulyBlocked)
        {
            var target = nodeById[group.Key];
            var rows = group.ToList();
            var sourceTitles = rows
                .Select(x => nodeById.TryGetValue(x.SourceNodeId, out var source) ? ResolveNodeTitle(source, assignments, courses) : string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToArray();

            string requirement;
            if (rows.Count == 1)
            {
                requirement = sourceTitles.Length == 1
                    ? $"Решите «{sourceTitles[0]}», чтобы открыть следующее задание."
                    : "Решите текущее задание, чтобы открыть продолжение.";
            }
            else if (sourceTitles.Length > 0)
            {
                requirement = $"Решите одно из предыдущих заданий ({string.Join(", ", sourceTitles.Select(x => $"«{x}»"))}), чтобы открыть продолжение.";
            }
            else
            {
                requirement = "Решите одно из предыдущих заданий, чтобы открыть продолжение.";
            }

            var lockId = CreateOpaqueId("locked", group.Key);
            outputNodes.Add(new
            {
                id = lockId,
                type = "locked",
                position = new { x = target.X, y = target.Y },
                settings = new
                {
                    title = "Продолжение закрыто",
                    requirement
                }
            });

            foreach (var row in rows)
            {
                outputEdges.Add(new
                {
                    id = CreateOpaqueId("locked-edge", $"{row.Edge.Id}\u001f{row.SourceNodeId}\u001f{row.TargetNodeId}"),
                    source = row.SourceNodeId,
                    sourceHandle = "out",
                    target = lockId,
                    targetHandle = "in",
                    settings = new { synthetic = true }
                });
            }
        }
    }

    private static string CreateOpaqueId(string prefix, string value)
    {
        // Stored node and edge ids often contain assignment GUIDs. Never expose
        // those identifiers through synthetic learner placeholders. A stable hash
        // keeps ReactFlow ids deterministic without leaking the hidden entity.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"{prefix}:{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static HashSet<Guid> BuildSubtreeCourseIds(Guid rootCourseId, IReadOnlyCollection<CourseTreeCourseDto> courses)
    {
        var children = courses
            .Where(x => x.ParentCourseId.HasValue)
            .GroupBy(x => x.ParentCourseId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToArray());
        var result = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(rootCourseId);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!result.Add(id)) continue;
            if (!children.TryGetValue(id, out var childIds)) continue;
            foreach (var childId in childIds) pending.Push(childId);
        }
        return result;
    }

    private static Dictionary<string, bool> ComputeThroughCompletion(
        HashSet<string> nodeIds,
        Dictionary<string, List<string>> incoming,
        Func<string, bool> selfComplete)
    {
        var indegree = nodeIds.ToDictionary(id => id, id => incoming.GetValueOrDefault(id)?.Count ?? 0, StringComparer.Ordinal);
        var queue = new Queue<string>(indegree.Where(x => x.Value == 0).Select(x => x.Key));
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        var outgoingIds = nodeIds.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (target, sources) in incoming)
        {
            foreach (var source in sources)
            {
                if (outgoingIds.TryGetValue(source, out var list)) list.Add(target);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var prerequisitesComplete = incoming[current].All(source => result.GetValueOrDefault(source));
            result[current] = prerequisitesComplete && selfComplete(current);
            foreach (var target in outgoingIds[current])
            {
                indegree[target]--;
                if (indegree[target] == 0) queue.Enqueue(target);
            }
        }

        foreach (var id in nodeIds)
        {
            if (!result.ContainsKey(id)) result[id] = false;
        }
        return result;
    }

    private static List<MapNode> ParseNodes(JsonElement document)
    {
        var result = new List<MapNode>();
        if (!document.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var node in nodesElement.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            var id = node.TryGetProperty("id", out var idElement) ? idElement.GetString()?.Trim() : null;
            var type = node.TryGetProperty("type", out var typeElement) ? typeElement.GetString()?.Trim() : null;
            Guid? entityId = null;
            if (node.TryGetProperty("entityId", out var entityElement) && entityElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(entityElement.GetString(), out var parsedEntity))
            {
                entityId = parsedEntity;
            }

            var x = 0d;
            var y = 0d;
            if (node.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Object)
            {
                if (position.TryGetProperty("x", out var xElement) && xElement.TryGetDouble(out var px)) x = px;
                if (position.TryGetProperty("y", out var yElement) && yElement.TryGetDouble(out var py)) y = py;
            }

            JsonElement? settings = null;
            if (node.TryGetProperty("settings", out var settingsElement) && settingsElement.ValueKind == JsonValueKind.Object)
                settings = settingsElement.Clone();

            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(type))
                result.Add(new MapNode(id, type, entityId, x, y, settings));
        }
        return result;
    }

    private static List<MapEdge> ParseEdges(JsonElement document, HashSet<string> knownNodeIds)
    {
        var result = new List<MapEdge>();
        if (!document.TryGetProperty("edges", out var edgesElement) || edgesElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var edge in edgesElement.EnumerateArray())
        {
            if (edge.ValueKind != JsonValueKind.Object) continue;
            var id = edge.TryGetProperty("id", out var idElement) ? idElement.GetString()?.Trim() : null;
            var source = edge.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString()?.Trim() : null;
            var target = edge.TryGetProperty("target", out var targetElement) ? targetElement.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) continue;
            if (!knownNodeIds.Contains(source) || !knownNodeIds.Contains(target)) continue;

            var legacyHiddenStart = false;
            var legacySequentialStart = false;
            var hiddenEffect = EffectTransition.Inherit;
            var sequentialEffect = EffectTransition.Inherit;
            JsonElement? settings = null;
            if (edge.TryGetProperty("settings", out var settingsElement) && settingsElement.ValueKind == JsonValueKind.Object)
            {
                settings = settingsElement.Clone();

                // Backward compatibility with maps saved by the first two progression
                // implementations. Legacy booleans become a START transition. As soon as
                // an editor touches the edge, the frontend rewrites it to the explicit
                // start/stop/inherit model.
                if (settingsElement.TryGetProperty("accessMode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String)
                {
                    var candidate = modeElement.GetString()?.Trim().ToLowerInvariant();
                    legacyHiddenStart = string.Equals(candidate, AccessModeAfterPrerequisites, StringComparison.Ordinal);
                    legacySequentialStart = string.Equals(candidate, AccessModeSequential, StringComparison.Ordinal);
                }

                if (settingsElement.TryGetProperty("gateUntilPrerequisites", out var gateElement)
                    && (gateElement.ValueKind == JsonValueKind.True || gateElement.ValueKind == JsonValueKind.False))
                {
                    legacyHiddenStart = gateElement.GetBoolean();
                }
                if (settingsElement.TryGetProperty("sequentialReveal", out var sequentialElement)
                    && (sequentialElement.ValueKind == JsonValueKind.True || sequentialElement.ValueKind == JsonValueKind.False))
                {
                    legacySequentialStart = sequentialElement.GetBoolean();
                }

                hiddenEffect = ParseEffectTransition(settingsElement, "hiddenEffect", legacyHiddenStart);
                sequentialEffect = ParseEffectTransition(settingsElement, "sequentialEffect", legacySequentialStart);
            }
            else
            {
                hiddenEffect = legacyHiddenStart ? EffectTransition.Start : EffectTransition.Inherit;
                sequentialEffect = legacySequentialStart ? EffectTransition.Start : EffectTransition.Inherit;
            }
            result.Add(new MapEdge(id, source, target, hiddenEffect, sequentialEffect, settings));
        }
        return result;
    }

    private static EffectTransition ParseEffectTransition(JsonElement settings, string propertyName, bool legacyStart)
    {
        if (settings.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString()?.Trim().ToLowerInvariant() switch
            {
                "start" => EffectTransition.Start,
                "stop" => EffectTransition.Stop,
                _ => EffectTransition.Inherit
            };
        }
        return legacyStart ? EffectTransition.Start : EffectTransition.Inherit;
    }

    private static bool ApplyEffect(bool current, EffectTransition transition)
        => transition switch
        {
            EffectTransition.Start => true,
            EffectTransition.Stop => false,
            _ => current
        };

    private static string ResolveNodeTitle(
        MapNode node,
        Dictionary<Guid, AssignmentProgressionRow> assignments,
        Dictionary<Guid, CourseTreeCourseDto> courses)
    {
        if (!node.EntityId.HasValue) return string.Empty;
        if (string.Equals(node.Type, "course", StringComparison.OrdinalIgnoreCase))
            return courses.TryGetValue(node.EntityId.Value, out var course) ? course.Title : string.Empty;
        return assignments.TryGetValue(node.EntityId.Value, out var assignment) ? assignment.Title : string.Empty;
    }
}
