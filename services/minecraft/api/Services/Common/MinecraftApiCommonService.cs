using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Contracts;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Domain;
using TaskForge.Minecraft.Api.Hubs;
using static TaskForge.Minecraft.Api.Services.Serialization.MinecraftApiSerializationService;

namespace TaskForge.Minecraft.Api.Services.Common;

internal static class MinecraftApiCommonService
{
    private static readonly Regex NickRx = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    internal sealed record MinecraftWebhookDeliveryResult(
        bool Attempted,
        bool Ok,
        string Message,
        int? StatusCode,
        string? RequestId,
        string? Uri,
        string? PlayerUuid);

    internal static string SecretFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "missing";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }

    internal static IResult Unauthorized() => Microsoft.AspNetCore.Http.Results.Json(new { message = "Сессия истекла или вы не вошли в систему.", code = "AUTH_REQUIRED" }, statusCode: StatusCodes.Status401Unauthorized);

    internal static IResult Forbidden() => Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к Minecraft-интеграции.", code = "MINECRAFT_FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);

    internal static string ServiceUrl(IConfiguration cfg, string name, string fallback) => (cfg[$"Services:{name}"] ?? cfg[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');

    internal static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
    {
        var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
    }

    internal static bool LooksLikeEmail(string value) => value.Contains('@') && value.Contains('.');

    internal static bool HasMinecraftAccess(HttpContext http) => http.User?.Identity?.IsAuthenticated == true && TaskForgeRequestSecurity.HasAnyRole(http.User, "Admin", "Minecraft");

    internal static Guid? UserId(HttpContext http, IConfiguration cfg) => TaskForgeRequestSecurity.UserId(http, cfg);

    internal static bool IsValidNick(string? value) => NickRx.IsMatch((value ?? string.Empty).Trim());

    internal static string NormalizeNick(string? value) => (value ?? string.Empty).Trim();

    internal static string GenerateCode()
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var chars = bytes.ToArray().Select(b => alphabet[b % alphabet.Length]).ToArray();
        return new string(chars[..4]) + "-" + new string(chars[4..]);
    }

    internal static (byte[] salt, byte[] hash) HashCode(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (salt, HashCodeWithSalt(code, salt));
    }

    internal static byte[] HashCodeWithSalt(string code, byte[] salt)
    {
        var saltB64 = Convert.ToBase64String(salt);
        var input = Encoding.UTF8.GetBytes((code ?? string.Empty).Trim().ToUpperInvariant() + ":" + saltB64);
        return SHA256.HashData(input);
    }

    internal static string? PluginKey(IConfiguration cfg) => cfg["MINECRAFT_PLUGIN_KEY"] ?? cfg["MINECRAFT_SERVER_KEY"];

    internal static bool IsPluginAuthorized(HttpContext http, IConfiguration cfg)
    {
        var key = PluginKey(cfg);
        if (string.IsNullOrWhiteSpace(key)) return false;
        var fromPlugin = http.Request.Headers["X-Minecraft-Key"].ToString();
        var fromTaskForge = http.Request.Headers["X-TaskForge-Key"].ToString();
        return string.Equals(fromPlugin, key, StringComparison.Ordinal)
            || string.Equals(fromTaskForge, key, StringComparison.Ordinal);
    }

    internal static int DeathChestCost(IConfiguration cfg)
        => Math.Clamp(cfg.GetValue<int?>("MINECRAFT_DEATH_CHEST_COST") ?? 50, 1, 100000);

    internal static int DeathTeleportCost(IConfiguration cfg)
        => Math.Clamp(cfg.GetValue<int?>("MINECRAFT_DEATH_TELEPORT_COST") ?? 100, 1, 100000);

    internal static int DeathCoordinatesCost(IConfiguration cfg)
        => Math.Clamp(cfg.GetValue<int?>("MINECRAFT_DEATH_COORDINATES_COST") ?? 10, 1, 100000);

    internal static async Task<MinecraftRatingBalanceDto> BuildMinecraftRatingBalanceAsync(
        Guid userId,
        MinecraftDbContext db,
        IConfiguration cfg,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var activity = await LoadActivitySummaryAsync(userId, cfg, httpFactory, ct);
        return await BuildMinecraftRatingBalanceAsync(userId, activity, db, cfg, ct);
    }

    internal static async Task<MinecraftRatingBalanceDto> BuildMinecraftRatingBalanceAsync(
        Guid userId,
        UserActivitySummaryDto activity,
        MinecraftDbContext db,
        IConfiguration cfg,
        CancellationToken ct)
    {
        var baseRating = activity.Score > 0 ? activity.Score : activity.Rating;
        var rows = await db.RatingTransactions.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
        var adjustment = rows.Sum(x => x.Delta);
        var spent = rows.Where(x => x.Delta < 0).Sum(x => -x.Delta);
        var restored = rows.Where(x => x.Delta > 0).Sum(x => x.Delta);
        var effective = baseRating + adjustment;
        return new MinecraftRatingBalanceDto(
            baseRating,
            adjustment,
            spent,
            restored,
            effective,
            Math.Max(0, effective),
            DeathCoordinatesCost(cfg),
            DeathChestCost(cfg),
            DeathTeleportCost(cfg));
    }

    internal static object ToRatingTransactionDto(MinecraftRatingTransaction x) => new
    {
        id = x.Id,
        userId = x.UserId,
        playerName = x.PlayerName,
        playerUuid = x.PlayerUuid,
        delta = x.Delta,
        kind = x.Kind,
        reason = x.Reason,
        requestId = x.RequestId,
        metadataJson = x.MetadataJson,
        actorUserId = x.ActorUserId,
        createdAtUtc = x.CreatedAtUtc
    };

    internal sealed record MinecraftRatingBalanceDto(
        int baseRating,
        int adjustmentTotal,
        int spentTotal,
        int restoredTotal,
        int effectiveRating,
        int balance,
        int deathCoordinatesCost,
        int deathChestCost,
        int deathTeleportCost);

    private static async Task<UserActivitySummaryDto?> ReadActivitySummaryAsync(
        Guid userId,
        string serviceName,
        string fallback,
        IConfiguration cfg,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient();
            using var msg = new HttpRequestMessage(
                HttpMethod.Get,
                $"{ServiceUrl(cfg, serviceName, fallback)}/api/internal/users/{userId}/activity-summary");
            AddInternalKey(msg, cfg);
            using var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<UserActivitySummaryDto>(JsonOptions(), ct);
        }
        catch
        {
            return null;
        }
    }

    private static UserActivitySummaryDto MergeActivitySummaries(
        UserActivitySummaryDto solutions,
        UserActivitySummaryDto tasks)
        => new()
        {
            SolvedAssignments = solutions.SolvedAssignments + tasks.SolvedAssignments,
            TotalAttempts = solutions.TotalAttempts + tasks.TotalAttempts,
            CodeSolutions = solutions.CodeSolutions,
            ImageSolutions = solutions.ImageSolutions,
            TestAttempts = tasks.TestAttempts,
            MathAttempts = tasks.MathAttempts,
            Score = solutions.Score + tasks.Score,
            Rating = solutions.Rating + tasks.Rating
        };

    internal static async Task<UserActivitySummaryDto> LoadActivitySummaryAsync(
        Guid userId,
        IConfiguration cfg,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var solutions = await ReadActivitySummaryAsync(
            userId,
            "SolutionsApi",
            "http://solutions-api:8080",
            cfg,
            httpFactory,
            ct) ?? new UserActivitySummaryDto();
        var tasks = await ReadActivitySummaryAsync(
            userId,
            "TasksApi",
            "http://tasks-api:8080",
            cfg,
            httpFactory,
            ct) ?? new UserActivitySummaryDto();
        return MergeActivitySummaries(solutions, tasks);
    }

    private static async Task<bool> ProbeServiceReadyAsync(
        string serviceName,
        string fallback,
        IConfiguration cfg,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient();
            using var response = await client.GetAsync(
                $"{ServiceUrl(cfg, serviceName, fallback)}/health/ready",
                ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    internal static async Task<bool> AreRatingBackendsHealthyAsync(
        IConfiguration cfg,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var solutionsTask = ProbeServiceReadyAsync(
            "SolutionsApi",
            "http://solutions-api:8080",
            cfg,
            httpFactory,
            ct);
        var tasksTask = ProbeServiceReadyAsync(
            "TasksApi",
            "http://tasks-api:8080",
            cfg,
            httpFactory,
            ct);
        await Task.WhenAll(solutionsTask, tasksTask);
        return await solutionsTask && await tasksTask;
    }

    internal static async Task<(bool Available, UserActivitySummaryDto Summary)> TryLoadActivitySummaryStrictAsync(
        Guid userId,
        IConfiguration cfg,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var solutionsTask = ReadActivitySummaryAsync(
            userId,
            "SolutionsApi",
            "http://solutions-api:8080",
            cfg,
            httpFactory,
            ct);
        var tasksTask = ReadActivitySummaryAsync(
            userId,
            "TasksApi",
            "http://tasks-api:8080",
            cfg,
            httpFactory,
            ct);

        await Task.WhenAll(solutionsTask, tasksTask);
        var solutions = await solutionsTask;
        var tasks = await tasksTask;
        if (solutions is null || tasks is null)
            return (false, new UserActivitySummaryDto());

        return (true, MergeActivitySummaries(solutions, tasks));
    }

    internal static async Task<MinecraftWebhookDeliveryResult> SendLinkCodeAsync(
        string nick,
        string code,
        IConfiguration cfg,
        IHttpClientFactory httpFactory,
        ILogger logger,
        CancellationToken ct)
    {
        var baseUrl = (cfg["MINECRAFT_WEBHOOK_BASE_URL"] ?? cfg["MINECRAFT_SERVER_URL"] ?? string.Empty).Trim();
        var path = (cfg["MINECRAFT_WEBHOOK_SEND_CODE_PATH"] ?? cfg["MINECRAFT_SERVER_LINK_PATH"] ?? "/taskforge/link/send").Trim();
        var key = cfg["MINECRAFT_WEBHOOK_KEY"] ?? cfg["MINECRAFT_SERVER_KEY"] ?? string.Empty;
        var requestId = Guid.NewGuid().ToString("N");

        logger.LogInformation(
            "[minecraft-webhook] preparing link-code delivery nick={Nick} requestId={RequestId} baseUrlPresent={BaseUrlPresent} baseUrl={BaseUrl} path={Path} keyPresent={KeyPresent} keyLen={KeyLength} keyFp={KeyFingerprint} codeFp={CodeFingerprint}",
            nick,
            requestId,
            !string.IsNullOrWhiteSpace(baseUrl),
            string.IsNullOrWhiteSpace(baseUrl) ? "<empty>" : baseUrl,
            path,
            !string.IsNullOrWhiteSpace(key),
            key.Length,
            SecretFingerprint(key),
            SecretFingerprint(code));

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogError(
                "[minecraft-webhook] delivery blocked: MINECRAFT_WEBHOOK_BASE_URL and MINECRAFT_SERVER_URL are empty requestId={RequestId} nick={Nick}",
                requestId,
                nick);
            return new(false, false, "адрес Minecraft-сервера не задан", null, requestId, null, null);
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBase)
            || (parsedBase.Scheme != Uri.UriSchemeHttp && parsedBase.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogError(
                "[minecraft-webhook] delivery blocked: invalid base URL requestId={RequestId} nick={Nick} baseUrl={BaseUrl}",
                requestId,
                nick,
                baseUrl);
            return new(false, false, "некорректный адрес Minecraft-сервера", null, requestId, baseUrl, null);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            logger.LogError(
                "[minecraft-webhook] delivery blocked: MINECRAFT_WEBHOOK_KEY and MINECRAFT_SERVER_KEY are empty requestId={RequestId} nick={Nick} baseUrl={BaseUrl}",
                requestId,
                nick,
                baseUrl);
            return new(false, false, "ключ webhook не задан", null, requestId, baseUrl, null);
        }

        var targetUri = new Uri(parsedBase.ToString().TrimEnd('/') + "/" + path.TrimStart('/'));
        var started = Stopwatch.GetTimestamp();

        try
        {
            var client = httpFactory.CreateClient("minecraft-webhook");
            client.Timeout = TimeSpan.FromSeconds(8);
            using var msg = new HttpRequestMessage(HttpMethod.Post, targetUri)
            {
                Content = JsonContent.Create(new { nick, code, ttlSeconds = 600 }, options: JsonOptions())
            };
            msg.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
            msg.Headers.TryAddWithoutValidation("X-TaskForge-Key", key);

            logger.LogInformation(
                "[minecraft-webhook] -> POST {Uri} requestId={RequestId} nick={Nick} timeoutSeconds={TimeoutSeconds}",
                targetUri,
                requestId,
                nick,
                client.Timeout.TotalSeconds);

            using var resp = await client.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            logger.LogInformation(
                "[minecraft-webhook] <- POST {Uri} requestId={RequestId} nick={Nick} status={StatusCode} elapsedMs={ElapsedMs:F1} body={Body}",
                targetUri,
                requestId,
                nick,
                (int)resp.StatusCode,
                elapsedMs,
                Short(body, 800));

            if (!resp.IsSuccessStatusCode)
            {
                return new(true, false, $"HTTP {(int)resp.StatusCode}: {Short(body, 300)}", (int)resp.StatusCode, requestId, targetUri.ToString(), null);
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var delivered = root.TryGetProperty("delivered", out var d) && d.ValueKind == JsonValueKind.True;
                var duplicate = root.TryGetProperty("duplicate", out var dup) && dup.ValueKind == JsonValueKind.True;
                var playerUuid = root.TryGetProperty("uuid", out var uuidElement) && uuidElement.ValueKind == JsonValueKind.String
                    ? uuidElement.GetString()?.Trim().ToLowerInvariant()
                    : null;
                logger.LogInformation(
                    "[minecraft-webhook] parsed delivery result requestId={RequestId} nick={Nick} delivered={Delivered} duplicate={Duplicate} playerUuidPresent={PlayerUuidPresent} playerUuid={PlayerUuid}",
                    requestId,
                    nick,
                    delivered,
                    duplicate,
                    !string.IsNullOrWhiteSpace(playerUuid),
                    playerUuid);
                if (delivered) return new(true, true, "delivered", (int)resp.StatusCode, requestId, targetUri.ToString(), playerUuid);
                if (duplicate) return new(true, true, "duplicate", (int)resp.StatusCode, requestId, targetUri.ToString(), playerUuid);
                if (root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    return new(true, false, reason.GetString() ?? "not delivered", (int)resp.StatusCode, requestId, targetUri.ToString(), playerUuid);
            }
            catch (Exception parseEx)
            {
                logger.LogWarning(
                    parseEx,
                    "[minecraft-webhook] response JSON parse failed requestId={RequestId} nick={Nick} uri={Uri} body={Body}",
                    requestId,
                    nick,
                    targetUri,
                    Short(body, 800));
            }

            return new(true, true, "sent", (int)resp.StatusCode, requestId, targetUri.ToString(), null);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            logger.LogError(
                ex,
                "[minecraft-webhook] timeout requestId={RequestId} nick={Nick} uri={Uri} elapsedMs={ElapsedMs:F1}",
                requestId,
                nick,
                targetUri,
                elapsedMs);
            return new(true, false, "тайм-аут подключения к Minecraft", null, requestId, targetUri.ToString(), null);
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            logger.LogError(
                ex,
                "[minecraft-webhook] delivery exception requestId={RequestId} nick={Nick} uri={Uri} elapsedMs={ElapsedMs:F1} exceptionType={ExceptionType} exceptionMessage={ExceptionMessage}",
                requestId,
                nick,
                targetUri,
                elapsedMs,
                ex.GetType().FullName,
                ex.Message);
            return new(true, false, $"{ex.GetType().Name}: {ex.Message}", null, requestId, targetUri.ToString(), null);
        }
    }

    internal static async Task AssignMinecraftRoleAsync(
        Guid userId,
        IConfiguration cfg,
        IHttpClientFactory httpFactory,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient();
            var identity = ServiceUrl(cfg, "IdentityApi", "http://identity-api:8080");
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{identity}/api/internal/feature-roles/users/{userId}/roles")
            {
                Content = JsonContent.Create(new RoleAssignRequest("Minecraft"), options: JsonOptions())
            };
            AddInternalKey(msg, cfg);
            using var response = await client.SendAsync(msg, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Failed to assign Minecraft role: user={UserId} status={StatusCode}",
                    userId,
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to assign Minecraft role: user={UserId}", userId);
        }
    }

    internal static async Task RemoveMinecraftRoleAsync(
        Guid userId,
        IConfiguration cfg,
        IHttpClientFactory httpFactory,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient();
            var identity = ServiceUrl(cfg, "IdentityApi", "http://identity-api:8080");
            using var msg = new HttpRequestMessage(HttpMethod.Delete, $"{identity}/api/internal/feature-roles/users/{userId}/roles/Minecraft");
            AddInternalKey(msg, cfg);
            using var response = await client.SendAsync(msg, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Failed to remove Minecraft role: user={UserId} status={StatusCode}",
                    userId,
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove Minecraft role: user={UserId}", userId);
        }
    }

    internal static async Task BroadcastAsync(IHubContext<MinecraftChatHub> hub, MinecraftChatMessage message, CancellationToken ct)
    {
        await hub.Clients.Group("minecraft-chat").SendAsync("ReceiveMessage", ToMinecraftChatDto(message), ct);
    }

    internal static object ToMinecraftChatDto(MinecraftChatMessage x) => new
    {
        id = x.Id,
        userId = x.UserId,
        source = x.Source,
        authorName = x.AuthorName,
        minecraftNick = x.MinecraftNick,
        minecraftUuid = x.MinecraftUuid,
        message = x.Message,
        createdAtUtc = x.CreatedAtUtc
    };

    internal static string NormalizeSource(string? kind)
    {
        var value = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "join" => "MinecraftJoin",
            "quit" => "MinecraftQuit",
            "advancement" => "MinecraftAdvancement",
            _ => "Minecraft"
        };
    }

    internal static string NormalizeMessage(string? message)
    {
        var text = (message ?? string.Empty).Trim();
        if (text.Length > 2000) text = text[..2000];
        return text;
    }

    internal static bool IsSuppressedMinecraftMessage(string? source, string? message)
    {
        if (!string.Equals((source ?? string.Empty).Trim(), "MinecraftAdvancement", StringComparison.OrdinalIgnoreCase)) return false;
        var lower = (message ?? string.Empty).Trim().ToLowerInvariant();
        return lower.Length == 0 || lower.Contains("recipes/") || lower.Contains("/root");
    }

    internal static async Task<int?> GetOnlinePlayersAsync(
        IConfiguration cfg,
        IHttpClientFactory httpFactory,
        ILogger logger,
        CancellationToken ct)
    {
        var baseUrl = (cfg["MINECRAFT_WEBHOOK_BASE_URL"] ?? cfg["MINECRAFT_SERVER_URL"] ?? string.Empty).Trim();
        var healthUrl = (cfg["MINECRAFT_HEALTH_URL"] ?? string.Empty).Trim();
        var endpoint = !string.IsNullOrWhiteSpace(healthUrl)
            ? healthUrl
            : (!string.IsNullOrWhiteSpace(baseUrl)
                ? new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "health").ToString()
                : null);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            logger.LogWarning("Minecraft health probe skipped: MINECRAFT_HEALTH_URL and MINECRAFT_WEBHOOK_BASE_URL are empty");
            return null;
        }

        var started = DateTimeOffset.UtcNow;
        logger.LogInformation("Minecraft health probe -> {Endpoint}", endpoint);
        try
        {
            var client = httpFactory.CreateClient("minecraft-health");
            client.Timeout = TimeSpan.FromSeconds(8);
            using var resp = await client.GetAsync(endpoint, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            logger.LogInformation(
                "Minecraft health probe <- {Endpoint} status={Status} elapsedMs={ElapsedMs} body={Body}",
                endpoint,
                (int)resp.StatusCode,
                (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                Short(body, 500));
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("onlinePlayers", out var online) && online.TryGetInt32(out var n))
                return n;
            logger.LogWarning("Minecraft health response did not contain integer onlinePlayers: endpoint={Endpoint} body={Body}", endpoint, Short(body, 500));
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Minecraft health probe failed: endpoint={Endpoint} elapsedMs={ElapsedMs}", endpoint, (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
            return null;
        }
    }

    private static string Short(string? value, int max)
    {
        var text = value ?? string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }
}
