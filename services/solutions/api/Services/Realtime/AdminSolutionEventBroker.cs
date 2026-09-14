using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TaskForge.Realtime;

namespace TaskForge.Solutions.Api.Services.Realtime;

internal sealed class AdminSolutionEventBroker(IServiceProvider services, ILogger<AdminSolutionEventBroker> log) : IHostedService, IAsyncDisposable
{
    private const int BacklogLimit = 200;
    private const int SubscriberCapacity = 256;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly LinkedList<AdminSolutionEvent> _backlog = new();
    private readonly ConcurrentDictionary<Guid, Channel<AdminSolutionEvent>> _subscribers = new();
    private ChannelMessageQueue? _redisSubscription;

    internal bool IsAvailable { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var redis = services.GetService<IConnectionMultiplexer>();
        if (redis is null)
        {
            log.LogWarning("Admin solution SSE is unavailable because Redis is not configured");
            return;
        }

        try
        {
            _redisSubscription = await redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(AdminSolutionEventPublisher.ChannelName));
            _redisSubscription.OnMessage(message =>
            {
                try
                {
                    var evt = JsonSerializer.Deserialize<AdminSolutionEvent>(message.Message.ToString(), JsonOptions);
                    if (evt is not null) Broadcast(evt);
                }
                catch (Exception ex)
                {
                    log.LogWarning("Admin solution live event decode failed ({ErrorType})", ex.GetType().Name);
                }
            });
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            log.LogWarning("Admin solution SSE Redis subscription failed ({ErrorType})", ex.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        IsAvailable = false;
        foreach (var channel in _subscribers.Values) channel.Writer.TryComplete();
        _subscribers.Clear();
        return Task.CompletedTask;
    }

    internal AdminSolutionSubscription Subscribe(string? lastEventId)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<AdminSolutionEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        List<AdminSolutionEvent> replay;
        lock (_gate)
        {
            _subscribers[id] = channel;
            var snapshot = _backlog.ToList();
            if (string.IsNullOrWhiteSpace(lastEventId))
            {
                replay = snapshot.TakeLast(100).ToList();
            }
            else
            {
                var index = snapshot.FindIndex(x => string.Equals(x.EventId, lastEventId, StringComparison.Ordinal));
                replay = index >= 0 ? snapshot.Skip(index + 1).ToList() : snapshot.TakeLast(100).ToList();
            }
        }

        return new AdminSolutionSubscription(id, channel.Reader, replay);
    }

    internal void Unsubscribe(Guid subscriptionId)
    {
        if (_subscribers.TryRemove(subscriptionId, out var channel)) channel.Writer.TryComplete();
    }

    internal void Broadcast(AdminSolutionEvent evt)
    {
        lock (_gate)
        {
            _backlog.AddLast(evt);
            while (_backlog.Count > BacklogLimit) _backlog.RemoveFirst();
            foreach (var channel in _subscribers.Values) channel.Writer.TryWrite(evt);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_redisSubscription is not null)
        {
            try { await _redisSubscription.UnsubscribeAsync(); } catch { }
        }
    }
}

internal sealed record AdminSolutionSubscription(
    Guid Id,
    ChannelReader<AdminSolutionEvent> Reader,
    IReadOnlyList<AdminSolutionEvent> Replay);
