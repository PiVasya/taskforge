using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TaskForge.Education.Api.Contracts;

namespace TaskForge.Education.Api.Services.CourseMaps;

public sealed class CourseMapPresenceStore
{
    private sealed record PresenceState(string ConnectionId, Guid UserId, string DisplayName, string? AvatarUrl, bool IsDirty, DateTimeOffset LastSeenAt);

    // Local fallback is used only when Redis is unavailable. Expire inactive
    // rooms globally, not just when that exact course is accessed again.
    private static readonly object LocalSync = new();
    private static readonly Dictionary<Guid, Dictionary<string, PresenceState>> Local = new();
    private const int MaxLocalRooms = 512;
    private const int MaxLocalEntriesPerRoom = 256;
    private static DateTimeOffset _nextSweepAt;
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

        lock (LocalSync)
        {
            PruneLocal(now);
            if (!Local.TryGetValue(rootCourseId, out var room))
            {
                if (Local.Count >= MaxLocalRooms)
                    return new[] { new CourseMapPresenceDto(userId, state.DisplayName, state.AvatarUrl, state.IsDirty, state.LastSeenAt) };
                room = new Dictionary<string, PresenceState>(StringComparer.Ordinal);
                Local.Add(rootCourseId, room);
            }
            PruneRoom(room, now);
            if (room.ContainsKey(connectionId) || room.Count < MaxLocalEntriesPerRoom)
                room[connectionId] = state;
            return Collapse(room.Values);
        }
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

        lock (LocalSync)
        {
            var now = DateTimeOffset.UtcNow;
            PruneLocal(now);
            if (!Local.TryGetValue(rootCourseId, out var room)) return Array.Empty<CourseMapPresenceDto>();
            PruneRoom(room, now);
            room.Remove(connectionId);
            if (room.Count == 0) Local.Remove(rootCourseId);
            return Collapse(room.Values);
        }
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

        lock (LocalSync)
        {
            var now = DateTimeOffset.UtcNow;
            PruneLocal(now);
            if (!Local.TryGetValue(rootCourseId, out var room)) return Array.Empty<CourseMapPresenceDto>();
            PruneRoom(room, now);
            if (room.Count == 0)
            {
                Local.Remove(rootCourseId);
                return Array.Empty<CourseMapPresenceDto>();
            }
            return Collapse(room.Values);
        }
    }

    private static async Task<IReadOnlyList<CourseMapPresenceDto>> ReadRedisAsync(IDatabase db, Guid rootCourseId, DateTimeOffset now)
    {
        var setKey = SetKey(rootCourseId);
        var cutoff = now.Subtract(PresenceTtl).ToUnixTimeMilliseconds();
        await db.SortedSetRemoveRangeByScoreAsync(setKey, double.NegativeInfinity, cutoff);
        // The newest presence entries are most useful to editors; never
        // materialize an unbounded Redis set in one HTTP response.
        var members = await db.SortedSetRangeByRankAsync(setKey, -MaxLocalEntriesPerRoom, -1, Order.Ascending);
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

    // Caller holds LocalSync. Scan at most once per 15 seconds, or whenever
    // the room table is full, and drop expired entries as well as empty rooms.
    private static void PruneLocal(DateTimeOffset now)
    {
        if (now < _nextSweepAt && Local.Count < MaxLocalRooms) return;
        _nextSweepAt = now.AddSeconds(15);
        foreach (var root in Local.Keys.ToArray())
        {
            var room = Local[root];
            PruneRoom(room, now);
            if (room.Count == 0) Local.Remove(root);
        }
    }

    private static void PruneRoom(Dictionary<string, PresenceState> room, DateTimeOffset now)
    {
        foreach (var entry in room.ToArray())
            if (now - entry.Value.LastSeenAt > PresenceTtl) room.Remove(entry.Key);
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
