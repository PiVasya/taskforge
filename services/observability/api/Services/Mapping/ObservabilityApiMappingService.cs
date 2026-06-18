using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;
using TaskForge.Observability.Api.Domain;

using TaskForge.Observability.Api.Contracts;
using static TaskForge.Observability.Api.Services.Common.ObservabilityApiCommonService;
using static TaskForge.Observability.Api.Services.Serialization.ObservabilityApiSerializationService;

namespace TaskForge.Observability.Api.Services.Mapping;

internal static class ObservabilityApiMappingService
{
    internal static async Task<Dictionary<Guid, UserSummaryDto>> LoadUserSummariesAsync(IEnumerable<Guid> userIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().Take(1000).ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, UserSummaryDto>();
        TaskForgeDebugTrace.UserSummaryRequest("observability-api", "identity-api", ids);
        try
        {
            var client = httpFactory.CreateClient();
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl(cfg, "IdentityApi", "http://identity-api:8080")}/api/internal/users/summaries")
            {
                Content = JsonContent.Create(new UserIdsRequest(ids), options: JsonOptions())
            };
            AddInternalKey(msg, cfg);
            using var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var empty = new Dictionary<Guid, UserSummaryDto>();
                TaskForgeDebugTrace.UserSummaryResponse("observability-api", "identity-api", ids, empty);
                return empty;
            }
            var rows = await resp.Content.ReadFromJsonAsync<List<UserSummaryDto>>(JsonOptions(), ct) ?? new List<UserSummaryDto>();
            var map = rows.Select(x => { x.Normalize(); return x; }).Where(x => x.UserId != Guid.Empty).GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First());
            TaskForgeDebugTrace.UserSummaryResponse("observability-api", "identity-api", ids, map);
            return map;
        }
        catch
        {
            var empty = new Dictionary<Guid, UserSummaryDto>();
            TaskForgeDebugTrace.UserSummaryResponse("observability-api", "identity-api", ids, empty);
            return empty;
        }
    }

    internal static ActivityItemDto ToActivityItem(PageView view, UserSummaryDto? user)
    {
        var category = ActivityCategory(view);
        var actionType = ActivityActionType(view);
        var target = NormalizeEndpoint(view.Path);
        var userDto = view.UserId.HasValue
            ? new ActivityUserDto
            {
                Id = view.UserId.Value,
                FullName = UserLabel(user),
                DisplayName = UserLabel(user),
                Email = user?.Email ?? user?.MaskedEmail,
                Role = user?.Role ?? "User",
            }
            : null;

        return new ActivityItemDto
        {
            Id = view.Id,
            UserId = view.UserId,
            Category = category,
            ActionType = actionType,
            Source = ActivitySource(view),
            Method = view.Method,
            Path = view.Path,
            Target = target,
            Description = ActivityDescription(view, category, actionType, target),
            StatusCode = view.StatusCode,
            IsAuthenticated = view.UserId.HasValue,
            CreatedAtUtc = view.CreatedAt,
            DurationMs = view.DurationMs,
            User = userDto,
        };
    }

    internal static string UserLabel(UserSummaryDto? user)
    {
        var name = (user?.DisplayName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(name) && !LooksLikeEmail(name)) return name;
        var full = string.Join(' ', new[] { user?.FirstName, user?.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (!string.IsNullOrWhiteSpace(full)) return full;
        var login = (user?.Login ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(login)) return login;
        var masked = (user?.MaskedEmail ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(masked)) return masked;
        return "Пользователь";
    }

    internal static string AssignmentLabel(string? path)
    {
        var p = NormalizeEndpoint(path);
        return string.IsNullOrWhiteSpace(p) ? "Задание без названия" : p;
    }

    internal static object[] BuildAlerts(int errors, int total, double avgLatency, double successRate)
    {
        var errorRate = Percent(errors, System.Math.Max(1, total));
        var alerts = new List<object>();
        if (errorRate > 10) alerts.Add(new { severity = "high", title = "Много ошибок API", message = $"За период {errorRate:0.0}% запросов завершились ошибкой." });
        if (avgLatency > 1000) alerts.Add(new { severity = "medium", title = "Высокая задержка", message = $"Средняя задержка backend около {avgLatency:0} мс." });
        if (successRate > 0 && successRate < 35) alerts.Add(new { severity = "medium", title = "Низкая успешность заданий", message = $"Успешность попыток по заданиям {successRate:0.0}%." });
        if (alerts.Count == 0) alerts.Add(new { severity = "good", title = "Критичных сигналов нет", message = "По собранной активности явных проблем не найдено." });
        return alerts.ToArray();
    }

}
