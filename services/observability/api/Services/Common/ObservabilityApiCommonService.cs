using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;
using TaskForge.Observability.Api.Domain;

using TaskForge.Observability.Api.Contracts;
using static TaskForge.Observability.Api.Services.Mapping.ObservabilityApiMappingService;
using static TaskForge.Observability.Api.Services.Serialization.ObservabilityApiSerializationService;

namespace TaskForge.Observability.Api.Services.Common;

internal static class ObservabilityApiCommonService
{
    internal static string ServiceUrl(IConfiguration cfg, string name, string fallback) => (cfg[$"Services:{name}"] ?? cfg[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');

    internal static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
    {
        var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
    }

    internal static string ActivityCategory(PageView view)
    {
        if (IsError(view)) return "error";
        if (Contains(view.Path, "/admin")) return "admin";
        if (IsLogin(view)) return "auth";
        if (IsAssignmentActivity(view)) return "assignment";
        if (Contains(view.Path, "/support")) return "support";
        if (string.Equals(view.Action, "page-view", StringComparison.OrdinalIgnoreCase) || string.Equals(view.Method, "GET", StringComparison.OrdinalIgnoreCase)) return "navigation";
        return "api";
    }

    internal static string ActivityActionType(PageView view)
    {
        if (!string.IsNullOrWhiteSpace(view.Action)) return view.Action!;
        if (!string.IsNullOrWhiteSpace(view.Method)) return view.Method!.ToUpperInvariant();
        return "event";
    }

    internal static string ActivitySource(PageView view) => ClientType(view);

    internal static string ActivityDescription(PageView view, string category, string actionType, string target)
    {
        var method = string.IsNullOrWhiteSpace(view.Method) ? "REQUEST" : view.Method!.ToUpperInvariant();
        var status = view.StatusCode.HasValue ? $", статус {view.StatusCode.Value}" : string.Empty;
        return category switch
        {
            "admin" => $"Действие в админке: {actionType} {target}{status}",
            "auth" => $"Действие авторизации: {actionType} {target}{status}",
            "assignment" => $"Активность по заданию: {actionType} {target}{status}",
            "support" => $"Активность поддержки: {actionType} {target}{status}",
            "error" => $"Ошибка запроса: {method} {target}{status}",
            "navigation" => $"Переход по странице: {target}{status}",
            _ => $"{method} {target}{status}",
        };
    }

    internal static bool LooksLikeEmail(string value) => value.Contains('@') && value.Contains('.');

    internal static bool Contains(string? value, string term) => (value ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase);

    internal static bool IsError(PageView v) => v.StatusCode is >= 400;

    internal static bool IsSuccess(PageView v) => v.StatusCode is null or >= 200 and < 300;

    internal static bool IsLogin(PageView v) => string.Equals(v.Action, "login", StringComparison.OrdinalIgnoreCase) || Contains(v.Path, "/login");

    internal static bool IsAssignmentActivity(PageView v) => Contains(v.Path, "assignment") || Contains(v.Path, "submit") || Contains(v.Path, "attempt") || Contains(v.Path, "task-test") || Contains(v.Path, "math-task") || Contains(v.Path, "image-test") || Contains(v.Path, "solutions");

    internal static string ClientType(PageView v)
    {
        if (Contains(v.Path, "/admin")) return "Админка";
        if (Contains(v.UserAgent, "bot")) return "Бот";
        if (string.IsNullOrWhiteSpace(v.UserAgent)) return "Внутренний клиент";
        return "Web";
    }

    internal static string AssignmentTypeFromPath(string? path)
    {
        if (Contains(path, "math")) return "math";
        if (Contains(path, "image")) return "image";
        if (Contains(path, "test")) return "test";
        return "code";
    }

    internal static string? LanguageFromPath(string? path)
    {
        var s = (path ?? string.Empty).ToLowerInvariant();
        foreach (var lang in new[] { "csharp", "cpp", "python", "javascript", "java", "pascal" }) if (s.Contains(lang)) return lang;
        return null;
    }

    internal static double AvgDuration(IEnumerable<PageView> rows)
    {
        var vals = rows.Select(x => x.DurationMs).Where(x => x.HasValue).Select(x => (double)x!.Value).ToList();
        return vals.Count == 0 ? 0 : Math.Round(vals.Average(), 1);
    }

    internal static double PercentileDuration(IEnumerable<PageView> rows, double p)
    {
        var vals = rows.Select(x => x.DurationMs).Where(x => x.HasValue).Select(x => (double)x!.Value).OrderBy(x => x).ToList();
        if (vals.Count == 0) return 0;
        var idx = Math.Clamp((int)Math.Ceiling(p * vals.Count) - 1, 0, vals.Count - 1);
        return vals[idx];
    }

    internal static double Percent(int num, int den) => den <= 0 ? 0 : Math.Round(num * 100.0 / den, 1);

    internal static object Point(DateTime date, double value) => new { label = date.ToString("dd.MM"), date = date.ToString("yyyy-MM-dd"), value = Math.Round(value, 1), count = Math.Round(value, 1) };

    internal static List<object> DayPoints(IEnumerable<PageView> rows, int days, Func<IEnumerable<PageView>, double> selector)
    {
        var byDay = rows.GroupBy(x => x.CreatedAt.UtcDateTime.Date).ToDictionary(x => x.Key, x => (IEnumerable<PageView>)x.ToList());
        var start = DateTime.UtcNow.Date.AddDays(-(days - 1));
        return Enumerable.Range(0, days).Select(i => { var day = start.AddDays(i); return Point(day, byDay.TryGetValue(day, out var vals) ? selector(vals) : 0); }).ToList();
    }

    internal static List<object> HourPoints(IEnumerable<PageView> rows)
    {
        var byHour = rows.GroupBy(x => x.CreatedAt.UtcDateTime.Hour).ToDictionary(x => x.Key, x => x.Count());
        return Enumerable.Range(0, 24).Select(h => new { label = $"{h:00}:00", value = byHour.GetValueOrDefault(h), count = byHour.GetValueOrDefault(h) }).Cast<object>().ToList();
    }

    internal static object Comparison(string label, double current, double previous, string? unit = null, bool percentMetric = false) => new { label, current, previous, unit, percentMetric, deltaPercent = previous == 0 ? (current == 0 ? 0 : 100) : Math.Round((current - previous) * 100.0 / previous, 1) };

}
