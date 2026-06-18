using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TaskForge.Files.Api.Data;
using TaskForge.Files.Api.Domain;

using TaskForge.Files.Api.Contracts;
using static TaskForge.Files.Api.Services.Access.FilesApiAccessService;
using static TaskForge.Files.Api.Services.Common.FilesApiCommonService;
using static TaskForge.Files.Api.Services.Image.FilesApiImageService;
using static TaskForge.Files.Api.Services.Mapping.FilesApiMappingService;

namespace TaskForge.Files.Api.Services.Serialization;

internal static class FilesApiSerializationService
{
    internal static bool CanReadPrivateKey(HttpContext http, string key)
    {
        if (IsEditorOrAdmin(http)) return true;
        var normalized = NormalizeKey(key);
        if (IsPublicFileKey(normalized)) return true;
        if (normalized.StartsWith("image-tests/reference/", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.StartsWith("image-tests/submissions/", StringComparison.OrdinalIgnoreCase))
        {
            var userId = CurrentUserId(http);
            if (userId is null) return false;
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // image-tests/submissions/{assignmentId:N}/{userId:N}/file.png
            return parts.Length >= 4 && string.Equals(parts[3], userId.Value.ToString("N"), StringComparison.OrdinalIgnoreCase);
        }
        // Fail closed by default. agent-conversations/ must later be checked via ai-api
        // or a shared ACL table; until then it must not be readable by every authenticated user.
        if (normalized.StartsWith("agent-conversations/", StringComparison.OrdinalIgnoreCase)) return false;
        return false;
    }

    internal static string NormalizeKey(string key) => (key ?? string.Empty).Replace('\\', '/').Trim().Trim('/');

    internal static string NormalizeFolder(string folder)
    {
        var value = NormalizeKey(folder);
        if (value.Contains("..", StringComparison.Ordinal)) return "editor-images";
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => new string(part.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray()))
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var normalized = string.Join("/", parts);
        return string.IsNullOrWhiteSpace(normalized) ? "editor-images" : normalized;
    }

}
