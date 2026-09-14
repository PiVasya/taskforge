using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace TaskForge.Realtime;

public sealed record AdminSolutionEvent(
    string EventId,
    string Kind,
    Guid ItemId,
    Guid UserId,
    Guid AssignmentId,
    string? Status,
    int? Score,
    DateTimeOffset OccurredAtUtc);

public sealed class AdminSolutionEventPublisher(IServiceProvider services, ILogger<AdminSolutionEventPublisher> log)
{
    public const string ChannelName = "tf:admin:solution-events:v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(
        string kind,
        Guid itemId,
        Guid userId,
        Guid assignmentId,
        string? status,
        int? score)
    {
        if (itemId == Guid.Empty || userId == Guid.Empty || assignmentId == Guid.Empty) return;
        var redis = services.GetService<IConnectionMultiplexer>();
        if (redis is null) return;

        var evt = new AdminSolutionEvent(
            Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(kind) ? "unknown" : kind.Trim().ToLowerInvariant(),
            itemId,
            userId,
            assignmentId,
            status,
            score,
            DateTimeOffset.UtcNow);

        try
        {
            var payload = JsonSerializer.Serialize(evt, JsonOptions);
            var publish = redis.GetSubscriber().PublishAsync(RedisChannel.Literal(ChannelName), payload);
            await publish.WaitAsync(TimeSpan.FromMilliseconds(300));
        }
        catch (TimeoutException)
        {
            log.LogDebug("Admin solution live event publish timed out");
        }
        catch (Exception ex)
        {
            log.LogWarning("Admin solution live event publish failed ({ErrorType})", ex.GetType().Name);
        }
    }
}
