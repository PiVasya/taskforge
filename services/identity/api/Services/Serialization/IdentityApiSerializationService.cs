using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using TaskForge.Identity.Api.Data;
using TaskForge.Identity.Api.Domain;

using TaskForge.Identity.Api.Contracts;
using static TaskForge.Identity.Api.Services.Access.IdentityApiAccessService;
using static TaskForge.Identity.Api.Services.Common.IdentityApiCommonService;
using static TaskForge.Identity.Api.Services.Image.IdentityApiImageService;
using static TaskForge.Identity.Api.Services.Mapping.IdentityApiMappingService;
using static TaskForge.Identity.Api.Services.Results.IdentityApiResultsService;

namespace TaskForge.Identity.Api.Services.Serialization;

internal static class IdentityApiSerializationService
{
    internal static string NormalizeSearch(string? value)
        => string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };

    internal static string NormalizeRole(string? role) => (role ?? "User").Trim() switch
    {
        "Admin" => "Admin",
        "Editor" => "Editor",
        "LearningEditor" => "LearningEditor",
        "Minecraft" => "Minecraft",
        _ => "User"
    };

    internal static string NormalizeRoleCode(string? role) => (role ?? string.Empty).Trim() switch
    {
        "admin" or "Admin" => "Admin",
        "editor" or "Editor" => "Editor",
        "learning-editor" or "LearningEditor" => "LearningEditor",
        "minecraft" or "Minecraft" => "Minecraft",
        var x => string.IsNullOrWhiteSpace(x) ? string.Empty : x
    };

    internal static string? NormalizeOptionalEmail(string? email)
    {
        var value = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value)) return null;
        var at = value.IndexOf('@');
        var lastAt = value.LastIndexOf('@');
        if (at <= 0 || at != lastAt || at >= value.Length - 3 || !value[(at + 1)..].Contains('.')) return null;
        return value.Length <= 320 ? value : null;
    }

    internal static string NormalizeEmail(string? email) => NormalizeOptionalEmail(email) ?? string.Empty;

    internal static string NormalizeLogin(string? login)
    {
        var raw = (login ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Contains('@')) raw = raw.Split('@', 2)[0];

        var sb = new StringBuilder(raw.Length);
        var prevDash = false;
        foreach (var ch in raw)
        {
            var allowed = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '.' || ch == '-';
            if (allowed)
            {
                sb.Append(ch);
                prevDash = ch == '-';
            }
            else if (!prevDash)
            {
                sb.Append('-');
                prevDash = true;
            }
        }

        return sb.ToString().Trim('.', '-', '_');
    }

    internal static string? ReadJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var s = value.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    internal static bool ReadJsonBool(JsonElement element, string propertyName, bool defaultValue)
    {
        if (element.ValueKind != JsonValueKind.Object) return defaultValue;
        if (!element.TryGetProperty(propertyName, out var value)) return defaultValue;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue
        };
    }

    internal static string DefaultUiSettingsJson() => "{\"colorTheme\":\"pink\",\"mode\":\"dark\",\"uiStyle\":\"default\",\"bgFx\":false,\"fxMode\":\"random\",\"fxVariant\":\"2\",\"codeSolveLayout\":\"split\",\"codeEditorStyle\":\"color\",\"showSidebarToggle\":true}";

    internal static string? ReadCookie(HttpContext http, string name) => http.Request.Cookies.TryGetValue(name, out var v) ? v : null;

    internal static string? ReadBearer(HttpContext http)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
    }

}
