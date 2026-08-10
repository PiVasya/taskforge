using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TaskForge.Education.Api.Contracts;

namespace TaskForge.Education.Api.Services.CourseMaps;

public sealed class CourseMapPresenceStore
{
    private sealed record PresenceState(string ConnectionId, Guid UserId, string DisplayName, string? AvatarUrl, bool IsDirty, DateTimeOffset LastSeenAt);

    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, PresenceState>> Local = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(45);
    private readonly IServiceProvider _services;

    public CourseMapPresenceStore(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<IReadOnlyList<CourseMapPresenceDto>> UpsertAsync(
        Guid rootCourseId,
        string connectionId,
        Guid userId,
        string displayName,
        string? avatarUrl,
        bool isDirty)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new PresenceState(connectionId, userId, Clean(displayName, "Пользователь", 120), CleanNullable(avatarUrl, 600), isDirty, now);
        var redis = _services.GetService<IConnectionMultiplexer>();
        if (redis is not null)
        {
            try
            {
                var db = redis.GetDatabase();
                var setKey = SetKey(rootCourseId);
                var itemKey = ItemKey(rootCourseId, connectionId);
                await db.StringSetAsync(itemKey, JsonSerializer.Serialize(state, JsonOptions), PresenceTtl);
                await db.SortedSetAddAsync(setKey, connectionId, now.ToUnixTimeMilliseconds());
                await db.KeyExpireAsync(setKey, TimeSpan.FromMinutes(10));
                return await ReadRedisAsync(db, rootCourseId, now);
            }
            catch
            {
                // Presence is best-effort. Editing must remain usable when Redis is unavailable.
            }
        }

        var room = Local.GetOrAdd(rootCourseId, _ => new ConcurrentDictionary<string, PresenceState>());
        room[connectionId] = state;
        PruneLocal(room, now);
        return Collapse(room.Values);
    }

    public async Task<IReadOnlyList<CourseMapPresenceDto>> RemoveAsync(Guid rootCourseId, string connectionId)
    {
        var redis = _services.GetService<IConnectionMultiplexer>();
        if (redis is not null)
        {
            try
            {
                var db = redis.GetDatabase();
                await db.KeyDeleteAsync(ItemKey(rootCourseId, connectionId));
                await db.SortedSetRemoveAsync(SetKey(rootCourseId), connectionId);
                return await ReadRedisAsync(db, rootCourseId, DateTimeOffset.UtcNow);
            }
            catch
            {
            }
        }

        if (!Local.TryGetValue(rootCourseId, out var room)) return Array.Empty<CourseMapPresenceDto>();
        room.TryRemove(connectionId, out _);
        PruneLocal(room, DateTimeOffset.UtcNow);
        if (room.IsEmpty) Local.TryRemove(rootCourseId, out _);
        return Collapse(room.Values);
    }

    public async Task<IReadOnlyList<CourseMapPresenceDto>> GetAsync(Guid rootCourseId)
    {
        var redis = _services.GetService<IConnectionMultiplexer>();
        if (redis is not null)
        {
            try
            {
                return await ReadRedisAsync(redis.GetDatabase(), rootCourseId, DateTimeOffset.UtcNow);
            }
            catch
            {
            }
        }

        if (!Local.TryGetValue(rootCourseId, out var room)) return Array.Empty<CourseMapPresenceDto>();
        PruneLocal(room, DateTimeOffset.UtcNow);
        return Collapse(room.Values);
    }

    private static async Task<IReadOnlyList<CourseMapPresenceDto>> ReadRedisAsync(IDatabase db, Guid rootCourseId, DateTimeOffset now)
    {
        var setKey = SetKey(rootCourseId);
        var cutoff = now.Subtract(PresenceTtl).ToUnixTimeMilliseconds();
        await db.SortedSetRemoveRangeByScoreAsync(setKey, double.NegativeInfinity, cutoff);
        var members = await db.SortedSetRangeByRankAsync(setKey, 0, -1, Order.Ascending);
        if (members.Length == 0) return Array.Empty<CourseMapPresenceDto>();

        var keys = members.Select(x => (RedisKey)ItemKey(rootCourseId, x.ToString())).ToArray();
        var values = await db.StringGetAsync(keys);
        var states = new List<PresenceState>();
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].IsNullOrEmpty)
            {
                await db.SortedSetRemoveAsync(setKey, members[i]);
                continue;
            }

            try
            {
                var state = JsonSerializer.Deserialize<PresenceState>(values[i].ToString(), JsonOptions);
                if (state is not null && now - state.LastSeenAt <= PresenceTtl) states.Add(state);
            }
            catch
            {
                await db.SortedSetRemoveAsync(setKey, members[i]);
            }
        }

        return Collapse(states);
    }

    private static void PruneLocal(ConcurrentDictionary<string, PresenceState> room, DateTimeOffset now)
    {
        foreach (var pair in room)
        {
            if (now - pair.Value.LastSeenAt > PresenceTtl) room.TryRemove(pair.Key, out _);
        }
    }

    private static IReadOnlyList<CourseMapPresenceDto> Collapse(IEnumerable<PresenceState> states)
        => states
            .GroupBy(x => x.UserId)
            .Select(group => group.OrderByDescending(x => x.LastSeenAt).First())
            .OrderByDescending(x => x.LastSeenAt)
            .Select(x => new CourseMapPresenceDto(x.UserId, x.DisplayName, x.AvatarUrl, x.IsDirty, x.LastSeenAt))
            .ToArray();

    private static string SetKey(Guid rootCourseId) => $"tf:course-map:presence:v1:{rootCourseId:N}";
    private static string ItemKey(Guid rootCourseId, string connectionId) => $"tf:course-map:presence:v1:{rootCourseId:N}:{connectionId}";

    private static string Clean(string? value, string fallback, int maxLength)
    {
        var normalized = string.Join(' ', (value ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(normalized)) normalized = fallback;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string? CleanNullable(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0) return null;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
