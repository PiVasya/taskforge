using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TaskForge.Realtime;
using TaskForge.Solutions.Api.Services.Realtime;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class AdminSolutionEventBrokerTests
{
    [Fact]
    public void NewSubscriber_ReplaysRecentEventsInOrder()
    {
        var broker = CreateBroker();
        var first = Event("first");
        var second = Event("second");
        broker.Broadcast(first);
        broker.Broadcast(second);

        var subscription = broker.Subscribe(null);

        Assert.Equal(new[] { first.EventId, second.EventId }, subscription.Replay.Select(x => x.EventId).ToArray());
        broker.Unsubscribe(subscription.Id);
    }

    [Fact]
    public void LastEventId_ReplaysOnlyEventsThatCameAfterIt()
    {
        var broker = CreateBroker();
        var first = Event("first");
        var second = Event("second");
        var third = Event("third");
        broker.Broadcast(first);
        broker.Broadcast(second);
        broker.Broadcast(third);

        var subscription = broker.Subscribe(second.EventId);

        Assert.Equal(new[] { third.EventId }, subscription.Replay.Select(x => x.EventId).ToArray());
        broker.Unsubscribe(subscription.Id);
    }

    [Fact]
    public async Task ActiveSubscriber_ReceivesBroadcastImmediately()
    {
        var broker = CreateBroker();
        var subscription = broker.Subscribe(null);
        var evt = Event("live");

        broker.Broadcast(evt);
        var received = await subscription.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(evt, received);
        broker.Unsubscribe(subscription.Id);
    }

    [Fact]
    public void Replay_IsBoundedToTheMostRecentHundredForFreshConnections()
    {
        var broker = CreateBroker();
        for (var i = 0; i < 230; i++) broker.Broadcast(Event($"e{i:000}"));

        var subscription = broker.Subscribe(null);

        Assert.Equal(100, subscription.Replay.Count);
        Assert.Equal("e130", subscription.Replay[0].Status);
        Assert.Equal("e229", subscription.Replay[^1].Status);
        broker.Unsubscribe(subscription.Id);
    }

    private static AdminSolutionEventBroker CreateBroker()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new AdminSolutionEventBroker(services, NullLogger<AdminSolutionEventBroker>.Instance);
    }

    private static AdminSolutionEvent Event(string status)
        => new(
            Guid.NewGuid().ToString("N"),
            "code",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            status,
            null,
            DateTimeOffset.UtcNow);
}
