using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Data;
using TaskForge.Support.Api.Domain;

using TaskForge.Support.Api.Contracts;
using TaskForge.Support.Api.Hubs;
using static TaskForge.Support.Api.Services.Mapping.SupportApiMappingService;
using static TaskForge.Support.Api.Services.Serialization.SupportApiSerializationService;

namespace TaskForge.Support.Api.Services.Common;

internal static class SupportApiCommonService
{
    internal static IResult Unauthorized() => Microsoft.AspNetCore.Http.Results.Json(new { message = "Сессия истекла или вы не вошли в систему.", code = "AUTH_REQUIRED" }, statusCode: StatusCodes.Status401Unauthorized);

    internal static string ServiceUrl(IConfiguration cfg, string name, string fallback) => (cfg[$"Services:{name}"] ?? cfg[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');

    internal static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
    {
        var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
    }

    internal static bool IsClosed(string? status) => string.Equals(status, "closed", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "resolved", StringComparison.OrdinalIgnoreCase);

    internal static string TicketType(string? subject)
    {
        var s = (subject ?? string.Empty).ToLowerInvariant();
        if (s.Contains("ошиб") || s.Contains("bug") || s.Contains("баг")) return "Ошибка";
        if (s.Contains("иде") || s.Contains("feature") || s.Contains("предлож")) return "Идея";
        if (s.Contains("вопрос") || s.Contains("help")) return "Вопрос";
        return "Обращение";
    }

    internal static bool LooksLikeEmail(string value) => value.Contains('@') && value.Contains('.');

}
