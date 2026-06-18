using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;

using TaskForge.Execution.Api.Contracts;
using static TaskForge.Execution.Api.Services.Mapping.ExecutionApiMappingService;
using static TaskForge.Execution.Api.Services.Results.ExecutionApiResultsService;

namespace TaskForge.Execution.Api.Services.Serialization;

internal static class ExecutionApiSerializationService
{
    internal static string NormalizeLanguage(string? lang) => (lang ?? "csharp").Trim().ToLowerInvariant() switch
    {
        "c#" or "cs" or "csharp" => "csharp",
        "c++" or "cpp" or "g++" or "gcc" or "cxx" => "cpp",
        "py" or "python" or "python3" => "python",
        "js" or "javascript" or "node" or "nodejs" or "node.js" => "javascript",
        "java" => "java",
        "pascal" or "pabc" => "pascal",
        var x => x
    };

    internal static string? StringArrayJson(IEnumerable<string>? values)
    {
        if (values == null) return null;
        var list = values.Select(x => (x ?? string.Empty).Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return list.Length == 0 ? null : JsonSerializer.Serialize(list);
    }

}
