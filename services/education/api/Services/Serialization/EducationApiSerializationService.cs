using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;

using TaskForge.Education.Api.Contracts;
using static TaskForge.Education.Api.Services.Access.EducationApiAccessService;
using static TaskForge.Education.Api.Services.Common.EducationApiCommonService;
using static TaskForge.Education.Api.Services.Mapping.EducationApiMappingService;

namespace TaskForge.Education.Api.Services.Serialization;

internal static class EducationApiSerializationService
{
    internal static string Serialize(IEnumerable<Guid>? ids) => JsonSerializer.Serialize((ids ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().ToArray());

    internal static Guid[] DeserializeIds(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? Array.Empty<Guid>() : JsonSerializer.Deserialize<Guid[]>(json) ?? Array.Empty<Guid>(); }
        catch { return Array.Empty<Guid>(); }
    }

}
