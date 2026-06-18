using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;
using TaskForge.Observability.Api.Domain;

using TaskForge.Observability.Api.Contracts;
using static TaskForge.Observability.Api.Services.Common.ObservabilityApiCommonService;
using static TaskForge.Observability.Api.Services.Mapping.ObservabilityApiMappingService;

namespace TaskForge.Observability.Api.Services.Serialization;

internal static class ObservabilityApiSerializationService
{
    internal static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };

    internal static string NormalizeSearch(string? value) => string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static string NormalizeEndpoint(string? path)
    {
        var p = (path ?? "/").Split('?', '#')[0];
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(x => Guid.TryParse(x, out _) ? "{id}" : x.Length > 24 ? "{value}" : x);
        return "/" + string.Join('/', parts);
    }

}
