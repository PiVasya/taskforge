using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace TaskForge.Realtime;

/// <summary>
/// Durable, region-independent admin SSE. YugabyteDB is the data source; no Redis,
/// in-memory event backlog, or broker subscription. Each API reads ONLY its own DB.
/// </summary>
public static class AdminSolutionFeedSse
{
    internal const int BatchSize = 200;
    internal const int ReplayCount = 100;
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    // Overlap handles a transaction committed later than one allocated earlier,
    // network retries, and small clock differences. Event IDs remove duplicates.
    internal static readonly TimeSpan CommitOverlap = TimeSpan.FromSeconds(45);
    private const int RememberedIds = 8192;
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    public static string Cursor(AdminSolutionFeedEntry entry) =>
        $"{entry.OccurredAtUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}:{entry.Id:N}";

    public static DateTimeOffset? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 90) return null;
        var parts = cursor.Split(':');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[1], "N", out _)) return null;
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis)) return null;
        try
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(millis);
            return time > DateTimeOffset.UtcNow.AddMinutes(5) ? null : time;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public static async Task StreamAsync(HttpContext http, DbContext db, ILogger logger, CancellationToken ct)
    {
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache, no-store";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var previous = ParseCursor(http.Request.Headers["Last-Event-ID"].ToString());
        // Bounded replay is only for a NEW browser. EventSource automatic reconnect
        // supplies Last-Event-ID and replays EVERY row after that cursor from DB.
        var seen = new HashSet<Guid>();
        var seenOrder = new Queue<Guid>();
        DateTimeOffset highWatermark;
        try
        {
            await http.Response.WriteAsync("retry: 2000\n\n", ct);
            if (previous is null)
            {
                var replay = await db.Set<AdminSolutionFeedEntry>().AsNoTracking()
                    .OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id)
                    .Take(ReplayCount).ToListAsync(ct);
                replay.Reverse();
                foreach (var entry in replay)
                {
                    await WriteEventAsync(http, entry, ct);
                    Remember(entry.Id, seen, seenOrder);
                }
                highWatermark = replay.Count > 0 ? replay[^1].OccurredAtUtc : DateTimeOffset.UtcNow;
            }
            else
            {
                highWatermark = previous.Value;
            }
            await http.Response.Body.FlushAsync(ct);
            var lastKeepAlive = DateTimeOffset.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                // Never wait on an in-memory queue. A disconnected region simply
                // resumes reading committed rows from YugabyteDB after reconnection.
                var from = highWatermark - CommitOverlap;
                var query = db.Set<AdminSolutionFeedEntry>().AsNoTracking()
                    .Where(x => x.OccurredAtUtc >= from)
                    .OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id);
                var count = 0;
                var offset = 0;
                while (true)
                {
                    var page = await query.Skip(offset).Take(BatchSize).ToListAsync(ct);
                    if (page.Count == 0) break;
                    offset += page.Count;
                    foreach (var entry in page)
                    {
                        if (entry.OccurredAtUtc > highWatermark) highWatermark = entry.OccurredAtUtc;
                        if (!seen.Add(entry.Id)) continue;
                        seenOrder.Enqueue(entry.Id);
                        while (seenOrder.Count > RememberedIds) seen.Remove(seenOrder.Dequeue());
                        await WriteEventAsync(http, entry, ct);
                        count++;
                    }
                    if (page.Count < BatchSize) break;
                }
                if (count > 0)
                {
                    await http.Response.Body.FlushAsync(ct);
                    lastKeepAlive = DateTimeOffset.UtcNow;
                }
                else if (DateTimeOffset.UtcNow - lastKeepAlive >= TimeSpan.FromSeconds(15))
                {
                    await http.Response.WriteAsync(": keepalive\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    lastKeepAlive = DateTimeOffset.UtcNow;
                }
                await Task.Delay(PollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Admin solution feed SSE query/connection failed; the browser will reconnect using Last-Event-ID");
            // Stop streaming: a new EventSource connection resumes at the last ID.
        }
    }

    private static void Remember(Guid id, HashSet<Guid> seen, Queue<Guid> order)
    {
        if (!seen.Add(id)) return;
        order.Enqueue(id);
        while (order.Count > RememberedIds) seen.Remove(order.Dequeue());
    }

    private static Task WriteEventAsync(HttpContext http, AdminSolutionFeedEntry entry, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(entry.ToWireEvent(), WireJson);
        return http.Response.WriteAsync($"id: {Cursor(entry)}\nevent: solution\ndata: {payload}\n\n", ct);
    }
}
