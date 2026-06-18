using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Domain;

using TaskForge.Minecraft.Api.Contracts;
using TaskForge.Minecraft.Api.Hubs;
using static TaskForge.Minecraft.Api.Services.Common.MinecraftApiCommonService;
using static TaskForge.Minecraft.Api.Services.Serialization.MinecraftApiSerializationService;

namespace TaskForge.Minecraft.Api.Services.Mapping;

internal static class MinecraftApiMappingService
{
    internal static async Task<Dictionary<Guid, UserSummaryDto>> LoadUserSummariesAsync(IEnumerable<Guid> userIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().Take(1000).ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, UserSummaryDto>();
        TaskForgeDebugTrace.UserSummaryRequest("minecraft-api", "identity-api", ids);
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
                TaskForgeDebugTrace.UserSummaryResponse("minecraft-api", "identity-api", ids, empty);
                return empty;
            }
            var rows = await resp.Content.ReadFromJsonAsync<List<UserSummaryDto>>(JsonOptions(), ct) ?? new List<UserSummaryDto>();
            var map = rows.Select(x => { x.Normalize(); return x; }).Where(x => x.UserId != Guid.Empty).GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First());
            TaskForgeDebugTrace.UserSummaryResponse("minecraft-api", "identity-api", ids, map);
            return map;
        }
        catch
        {
            var empty = new Dictionary<Guid, UserSummaryDto>();
            TaskForgeDebugTrace.UserSummaryResponse("minecraft-api", "identity-api", ids, empty);
            return empty;
        }
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

}
