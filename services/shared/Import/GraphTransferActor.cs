using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace TaskForge.Shared.Import;

// This capability exists only inside a trusted service invocation. No request
// header, cookie, serialized claim, or client-supplied role can create it.
public static class GraphTransferActor
{
    private static readonly object ActorKey = new();
    public static ClaimsPrincipal? Get(HttpContext http) => http.Items.TryGetValue(ActorKey, out var value)
        ? value as ClaimsPrincipal : null;

    public static void AddInternalKey(HttpRequestMessage request, IConfiguration cfg)
    {
        var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"]
            ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("INTERNAL_KEY_UNAVAILABLE");
        request.Headers.TryAddWithoutValidation("X-Internal-Key", key);
    }

    public static async Task<bool> RestoreAsync(HttpContext http, Guid userId, IConfiguration cfg,
        IHttpClientFactory clients, CancellationToken ct)
    {
        if (userId == Guid.Empty) return false;
        var url = (cfg["Services:IdentityApi"] ?? cfg["ServiceUrls:IdentityApi"] ?? "http://identity-api:8080").TrimEnd('/');
        using var rolesRequest = new HttpRequestMessage(HttpMethod.Post, url + "/api/internal/users/access-levels")
        { Content = JsonContent.Create(new { userIds = new[] { userId } }) };
        AddInternalKey(rolesRequest, cfg);
        using var rolesResponse = await clients.CreateClient().SendAsync(rolesRequest, ct);
        rolesResponse.EnsureSuccessStatusCode();
        var levels = await rolesResponse.Content.ReadFromJsonAsync<ActorLevel[]>(cancellationToken: ct);
        var actor = levels?.SingleOrDefault(x => x.UserId == userId);
        if (actor?.CanAuthorCourses != true) return false;
        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, url + $"/api/internal/account-lifecycle/users/{userId:D}");
        AddInternalKey(statusRequest, cfg);
        using var statusResponse = await clients.CreateClient().SendAsync(statusRequest, ct);
        if (statusResponse.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        statusResponse.EnsureSuccessStatusCode();
        var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (!IsActive(status)) return false;
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString("D")), new("sub", userId.ToString("D")) };
        claims.AddRange((actor.Roles ?? []).Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "GraphTransferWorker"));
        http.Items[ActorKey] = principal;
        http.User = principal;
        return true;
    }

    public static bool IsActive(JsonElement status) => status.ValueKind == JsonValueKind.Object
        && status.TryGetProperty("blocked", out var blocked) && blocked.ValueKind == JsonValueKind.False
        && status.TryGetProperty("accountStatus", out var state) && state.ValueKind == JsonValueKind.String
        && string.Equals(state.GetString(), "active", StringComparison.OrdinalIgnoreCase)
        && (!status.TryGetProperty("deletedAtUtc", out var deleted) || deleted.ValueKind == JsonValueKind.Null)
        && (!status.TryGetProperty("mergedIntoUserId", out var merged) || merged.ValueKind == JsonValueKind.Null);

    private sealed record ActorLevel(Guid UserId, string[] Roles, bool CanAuthorCourses);
}
