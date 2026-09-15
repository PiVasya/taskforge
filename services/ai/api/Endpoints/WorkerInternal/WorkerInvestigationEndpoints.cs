using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Contracts;
using TaskForge.Ai.Api.Data;

using static TaskForge.Ai.Api.Services.Common.AiApiCommonService;

namespace TaskForge.Ai.Api.Endpoints;

internal static partial class AiApiEndpoints
{
    private static WebApplication MapWorkerInvestigationEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/agent/tools/investigate", async (
            AgentInvestigationToolRequest request,
            AiDbContext db,
            IHttpClientFactory httpFactory,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            if (!request.RunId.HasValue || request.RunId == Guid.Empty || string.IsNullOrWhiteSpace(request.WorkerId))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "runId и workerId обязательны.", code = "AI_TOOL_RUN_REQUIRED" });

            var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.RunId.Value, ct);
            if (run == null)
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Обработка не найдена.", code = "AI_RUN_NOT_FOUND" });
            if (!string.Equals(run.WorkerId, request.WorkerId, StringComparison.Ordinal) || !string.Equals(run.Status, "running", StringComparison.OrdinalIgnoreCase))
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Worker lease не подтверждён.", code = "AI_TOOL_LEASE_INVALID" }, statusCode: StatusCodes.Status403Forbidden);

            var conversation = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == run.ConversationId, ct);
            if (conversation?.UserId is not Guid ownerUserId || ownerUserId == Guid.Empty)
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "У диалога нет владельца.", code = "AI_TOOL_OWNER_MISSING" }, statusCode: StatusCodes.Status403Forbidden);

            var identity = await PostInternalJsonAsync(
                httpFactory,
                cfg,
                $"{ServiceBase(cfg, "IdentityApi", "http://identity-api:8080")}/api/internal/users/summaries",
                new { userIds = new[] { ownerUserId } },
                ct);
            var owner = identity is JsonArray { Count: > 0 } owners ? owners[0] as JsonObject : null;
            var ownerRole = owner?["role"]?.ToString();
            if (!string.Equals(ownerRole, "Admin", StringComparison.OrdinalIgnoreCase))
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Инструменты расследования доступны только администратору.", code = "AI_INVESTIGATION_ADMIN_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);

            var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
            var take = System.Math.Clamp(request.Take ?? 200, 1, 500);
            var to = request.ToUtc ?? DateTimeOffset.UtcNow;
            var from = request.FromUtc ?? to.AddHours(-24);

            try
            {
                switch (action)
                {
                    case "get_support_chat":
                    {
                        var ticketId = request.SupportTicketId ?? conversation.SupportTicketId;
                        if (!ticketId.HasValue || ticketId == Guid.Empty)
                            return MissingArgument("supportTicketId");
                        var ticket = await GetInternalJsonAsync(httpFactory, cfg, $"{ServiceBase(cfg, "SupportApi", "http://support-api:8080")}/api/internal/agent/support/{ticketId:D}", ct);
                        return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, action, ticket });
                    }
                    case "investigate_support_ticket":
                    {
                        var ticketId = request.SupportTicketId ?? conversation.SupportTicketId;
                        if (!ticketId.HasValue || ticketId == Guid.Empty)
                            return MissingArgument("supportTicketId");

                        var ticket = await GetInternalJsonAsync(httpFactory, cfg, $"{ServiceBase(cfg, "SupportApi", "http://support-api:8080")}/api/internal/agent/support/{ticketId:D}", ct);
                        var userId = ReadGuid(ticket, "userId");
                        if (!userId.HasValue || userId == Guid.Empty)
                            return Microsoft.AspNetCore.Http.Results.Json(new { message = "У обращения нет пользователя.", code = "SUPPORT_USER_MISSING" }, statusCode: StatusCodes.Status409Conflict);

                        var activityTask = GetInternalJsonAsync(httpFactory, cfg, BuildTasksActivityUrl(cfg, userId.Value, from, to, take), ct);
                        var codeTask = GetInternalJsonAsync(httpFactory, cfg, BuildSolutionsIndexUrl(cfg, userId.Value, request.AssignmentId, from, to, take), ct);
                        var attemptsTask = GetInternalJsonAsync(httpFactory, cfg, BuildAttemptsIndexUrl(cfg, userId.Value, request.AssignmentId, from, to, take), ct);
                        await Task.WhenAll(activityTask, codeTask, attemptsTask);

                        var combined = BuildCombinedSolutionIndex(codeTask.Result, attemptsTask.Result, take);
                        var timeline = BuildInvestigationTimeline(ticket, activityTask.Result, combined, System.Math.Clamp(take * 2, 1, 500));
                        return Microsoft.AspNetCore.Http.Results.Ok(new
                        {
                            ok = true,
                            action,
                            ticketId,
                            userId,
                            window = new { fromUtc = from, toUtc = to },
                            ticket,
                            activity = activityTask.Result,
                            solutions = combined,
                            timeline
                        });
                    }
                    case "get_user_recent_activity":
                    {
                        if (!request.UserId.HasValue || request.UserId == Guid.Empty)
                            return MissingArgument("userId");
                        var activity = await GetInternalJsonAsync(httpFactory, cfg, BuildTasksActivityUrl(cfg, request.UserId.Value, from, to, take), ct);
                        return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, action, activity });
                    }
                    case "list_user_solutions":
                    {
                        if (!request.UserId.HasValue || request.UserId == Guid.Empty)
                            return MissingArgument("userId");
                        var codeTask = GetInternalJsonAsync(httpFactory, cfg, BuildSolutionsIndexUrl(cfg, request.UserId.Value, request.AssignmentId, from, to, take), ct);
                        var attemptsTask = GetInternalJsonAsync(httpFactory, cfg, BuildAttemptsIndexUrl(cfg, request.UserId.Value, request.AssignmentId, from, to, take), ct);
                        await Task.WhenAll(codeTask, attemptsTask);
                        var combined = BuildCombinedSolutionIndex(codeTask.Result, attemptsTask.Result, take);
                        return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, action, userId = request.UserId, assignmentId = request.AssignmentId, window = new { fromUtc = from, toUtc = to }, solutions = combined });
                    }
                    case "get_solution":
                    {
                        if (!request.ItemId.HasValue || request.ItemId == Guid.Empty)
                            return MissingArgument("itemId");
                        var kind = (request.Kind ?? string.Empty).Trim().ToLowerInvariant();
                        if (kind is "code" or "sql" or "image")
                        {
                            var detail = await GetInternalJsonAsync(httpFactory, cfg, $"{ServiceBase(cfg, "SolutionsApi", "http://solutions-api:8080")}/api/internal/agent/solutions/{Uri.EscapeDataString(kind)}/{request.ItemId:D}", ct);
                            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, action, kind, detail });
                        }
                        if (kind is "test" or "math")
                        {
                            var detail = await GetInternalJsonAsync(httpFactory, cfg, $"{ServiceBase(cfg, "TasksApi", "http://tasks-api:8080")}/api/internal/agent/attempts/{Uri.EscapeDataString(kind)}/{request.ItemId:D}", ct);
                            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, action, kind, detail });
                        }
                        return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "kind должен быть code, sql, image, test или math.", code = "AI_TOOL_KIND_INVALID" });
                    }
                    case "get_assignment":
                    {
                        if (!request.AssignmentId.HasValue || request.AssignmentId == Guid.Empty)
                            return MissingArgument("assignmentId");
                        var assignment = await GetInternalJsonAsync(httpFactory, cfg, $"{ServiceBase(cfg, "TasksApi", "http://tasks-api:8080")}/api/internal/agent/assignments/{request.AssignmentId:D}", ct);
                        return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, action, assignment });
                    }
                    default:
                        return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Неизвестный read-only investigation action.", code = "AI_TOOL_ACTION_INVALID" });
                }
            }
            catch (InternalToolHttpException ex)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new
                {
                    message = "Внутренний сервис не смог вернуть данные для расследования.",
                    code = "AI_INVESTIGATION_UPSTREAM_FAILED",
                    upstreamStatus = (int)ex.StatusCode,
                    upstream = ex.Service
                }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        return app;
    }

    private static IResult MissingArgument(string name) => Microsoft.AspNetCore.Http.Results.BadRequest(new
    {
        message = $"Не передан {name}.",
        code = "AI_TOOL_ARGUMENT_REQUIRED",
        argument = name
    });

    private static string ServiceBase(IConfiguration cfg, string name, string fallback) =>
        (cfg[$"Services:{name}"] ?? fallback).TrimEnd('/');

    private static string BuildTasksActivityUrl(IConfiguration cfg, Guid userId, DateTimeOffset from, DateTimeOffset to, int take) =>
        $"{ServiceBase(cfg, "TasksApi", "http://tasks-api:8080")}/api/internal/agent/users/{userId:D}/activity?fromUtc={Uri.EscapeDataString(from.ToString("O"))}&toUtc={Uri.EscapeDataString(to.ToString("O"))}&take={take}";

    private static string BuildSolutionsIndexUrl(IConfiguration cfg, Guid userId, Guid? assignmentId, DateTimeOffset from, DateTimeOffset to, int take)
    {
        var assignment = assignmentId.HasValue ? $"&assignmentId={assignmentId:D}" : string.Empty;
        return $"{ServiceBase(cfg, "SolutionsApi", "http://solutions-api:8080")}/api/internal/agent/users/{userId:D}/solutions?fromUtc={Uri.EscapeDataString(from.ToString("O"))}&toUtc={Uri.EscapeDataString(to.ToString("O"))}&take={take}{assignment}";
    }

    private static string BuildAttemptsIndexUrl(IConfiguration cfg, Guid userId, Guid? assignmentId, DateTimeOffset from, DateTimeOffset to, int take)
    {
        var assignment = assignmentId.HasValue ? $"&assignmentId={assignmentId:D}" : string.Empty;
        return $"{ServiceBase(cfg, "TasksApi", "http://tasks-api:8080")}/api/internal/agent/users/{userId:D}/attempts?fromUtc={Uri.EscapeDataString(from.ToString("O"))}&toUtc={Uri.EscapeDataString(to.ToString("O"))}&take={take}{assignment}";
    }

    private static JsonObject BuildCombinedSolutionIndex(JsonNode? codePayload, JsonNode? attemptPayload, int take)
    {
        var items = new List<JsonObject>();
        AppendIndexItems(items, codePayload?["solutions"] as JsonArray, "solutions-api");
        AppendIndexItems(items, attemptPayload?["attempts"] as JsonArray, "tasks-api");
        var effectiveTake = System.Math.Clamp(take, 1, 500);
        var truncated = items.Count > effectiveTake || ReadBoolean(codePayload, "truncated") || ReadBoolean(attemptPayload, "truncated");
        var ordered = items
            .OrderByDescending(ReadTimestamp)
            .Take(effectiveTake)
            .ToList();

        var kinds = ordered.GroupBy(x => x["kind"]?.ToString() ?? "unknown", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var statuses = ordered.GroupBy(x => x["status"]?.ToString() ?? "unknown", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var assignmentSummaries = ordered
            .Where(x => ReadGuid(x, "assignmentId") is Guid assignmentId && assignmentId != Guid.Empty)
            .GroupBy(x => x["assignmentId"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group.OrderByDescending(ReadTimestamp).ToList();
                var first = rows[0];
                var groupKinds = rows.GroupBy(x => x["kind"]?.ToString() ?? "unknown", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
                var groupStatuses = rows.GroupBy(x => x["status"]?.ToString() ?? "unknown", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
                var accepted = rows.Count(x => IsAcceptedStatus(x["status"]?.ToString()));
                return new JsonObject
                {
                    ["assignmentId"] = group.Key,
                    ["assignmentTitle"] = first["assignmentTitle"]?.DeepClone(),
                    ["assignmentUrl"] = first["assignmentUrl"]?.DeepClone(),
                    ["count"] = rows.Count,
                    ["acceptedCount"] = accepted,
                    ["problemCount"] = rows.Count - accepted,
                    ["kinds"] = JsonSerializer.SerializeToNode(groupKinds),
                    ["statuses"] = JsonSerializer.SerializeToNode(groupStatuses),
                    ["firstSubmittedAtUtc"] = rows.Min(ReadTimestamp).ToString("O"),
                    ["lastSubmittedAtUtc"] = rows.Max(ReadTimestamp).ToString("O")
                };
            })
            .OrderByDescending(x => DateTimeOffset.TryParse(x["lastSubmittedAtUtc"]?.ToString(), out var value) ? value : DateTimeOffset.MinValue)
            .ToList();

        return new JsonObject
        {
            ["count"] = ordered.Count,
            ["limit"] = effectiveTake,
            ["truncated"] = truncated,
            ["assignmentCount"] = assignmentSummaries.Count,
            ["kinds"] = JsonSerializer.SerializeToNode(kinds),
            ["statuses"] = JsonSerializer.SerializeToNode(statuses),
            ["assignments"] = new JsonArray(assignmentSummaries.Select(x => (JsonNode?)x.DeepClone()).ToArray()),
            ["items"] = new JsonArray(ordered.Select(x => (JsonNode?)x.DeepClone()).ToArray())
        };
    }

    private static JsonObject BuildInvestigationTimeline(JsonNode? ticketPayload, JsonNode? activityPayload, JsonObject solutions, int take)
    {
        var entries = new List<JsonObject>();

        if (ticketPayload?["messages"] is JsonArray messages)
        {
            foreach (var message in messages.OfType<JsonObject>())
            {
                entries.Add(new JsonObject
                {
                    ["type"] = "support_message",
                    ["occurredAtUtc"] = FirstString(message, "createdAtUtc", "createdAt"),
                    ["messageId"] = FirstString(message, "messageId", "id"),
                    ["userId"] = FirstString(message, "userId"),
                    ["authorRole"] = FirstString(message, "authorRole"),
                    ["text"] = TrimForTimeline(FirstString(message, "text", "body"), 400)
                });
            }
        }

        if (activityPayload?["events"] is JsonArray events)
        {
            foreach (var evt in events.OfType<JsonObject>())
            {
                entries.Add(new JsonObject
                {
                    ["type"] = "assignment_activity",
                    ["occurredAtUtc"] = FirstString(evt, "createdAtUtc", "createdAt"),
                    ["eventType"] = FirstString(evt, "eventType"),
                    ["assignmentId"] = FirstString(evt, "assignmentId"),
                    ["assignmentTitle"] = FirstString(evt, "assignmentTitle"),
                    ["assignmentUrl"] = FirstString(evt, "assignmentUrl"),
                    ["attemptId"] = FirstString(evt, "attemptId"),
                    ["submissionId"] = FirstString(evt, "submissionId"),
                    ["language"] = FirstString(evt, "language")
                });
            }
        }

        if (solutions["items"] is JsonArray solutionItems)
        {
            foreach (var item in solutionItems.OfType<JsonObject>())
            {
                entries.Add(new JsonObject
                {
                    ["type"] = "solution",
                    ["occurredAtUtc"] = FirstString(item, "submittedAtUtc", "submittedAt", "createdAtUtc", "createdAt"),
                    ["itemId"] = FirstString(item, "id", "attemptId"),
                    ["kind"] = FirstString(item, "kind"),
                    ["assignmentId"] = FirstString(item, "assignmentId", "taskAssignmentId"),
                    ["assignmentTitle"] = FirstString(item, "assignmentTitle"),
                    ["assignmentUrl"] = FirstString(item, "assignmentUrl"),
                    ["status"] = FirstString(item, "status"),
                    ["score"] = item["score"]?.DeepClone() ?? item["scorePercent"]?.DeepClone(),
                    ["language"] = FirstString(item, "language")
                });
            }
        }

        var ordered = entries
            .OrderBy(x => ReadTimestamp(x))
            .TakeLast(System.Math.Clamp(take, 1, 500))
            .ToList();
        return new JsonObject
        {
            ["count"] = ordered.Count,
            ["items"] = new JsonArray(ordered.Select(x => (JsonNode?)x.DeepClone()).ToArray())
        };
    }

    private static string? FirstString(JsonObject node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = node[name]?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string? TrimForTimeline(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        return text.Length <= max ? text : text[..System.Math.Max(0, max - 3)] + "...";
    }

    private static bool IsAcceptedStatus(string? status)
    {
        var value = (status ?? string.Empty).Trim();
        return value.Equals("accepted", StringComparison.OrdinalIgnoreCase)
            || value.Equals("passed", StringComparison.OrdinalIgnoreCase)
            || value.Equals("success", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ok", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendIndexItems(List<JsonObject> target, JsonArray? source, string service)
    {
        if (source == null) return;
        foreach (var node in source.OfType<JsonObject>())
        {
            var copy = node.DeepClone().AsObject();
            copy["sourceService"] = service;
            target.Add(copy);
        }
    }

    private static DateTimeOffset ReadTimestamp(JsonObject node)
    {
        foreach (var key in new[] { "occurredAtUtc", "submittedAtUtc", "submittedAt", "createdAtUtc", "createdAt" })
        {
            if (DateTimeOffset.TryParse(node[key]?.ToString(), out var value)) return value;
        }
        return DateTimeOffset.MinValue;
    }

    private static bool ReadBoolean(JsonNode? node, string property)
    {
        if (node is not JsonObject obj) return false;
        return bool.TryParse(obj[property]?.ToString(), out var value) && value;
    }

    private static Guid? ReadGuid(JsonNode? node, string property)
    {
        if (node is not JsonObject obj) return null;
        return Guid.TryParse(obj[property]?.ToString(), out var value) ? value : null;
    }

    private static async Task<JsonNode?> GetInternalJsonAsync(IHttpClientFactory factory, IConfiguration cfg, string url, CancellationToken ct)
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddInternalKey(request, cfg);
        using var response = await client.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InternalToolHttpException(response.StatusCode, new Uri(url).Host);
        return string.IsNullOrWhiteSpace(raw) ? new JsonObject() : JsonNode.Parse(raw);
    }

    private static async Task<JsonNode?> PostInternalJsonAsync(IHttpClientFactory factory, IConfiguration cfg, string url, object body, CancellationToken ct)
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        AddInternalKey(request, cfg);
        using var response = await client.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InternalToolHttpException(response.StatusCode, new Uri(url).Host);
        return string.IsNullOrWhiteSpace(raw) ? new JsonObject() : JsonNode.Parse(raw);
    }

    private sealed class InternalToolHttpException(HttpStatusCode statusCode, string service) : Exception
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
        public string Service { get; } = service;
    }
}
