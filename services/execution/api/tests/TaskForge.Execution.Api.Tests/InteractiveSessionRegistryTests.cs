using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TaskForge.Execution.Api.Services.Interactive;
using Xunit;

namespace TaskForge.Execution.Api.Tests;

public sealed class InteractiveSessionRegistryTests
{
    private static InteractiveSessionRegistry LocalRegistry() =>
        new(new ConfigurationBuilder().Build());

    private static (InteractiveSessionPayload Payload, string Ticket)? Create(
        InteractiveSessionRegistry registry, Guid userId, bool isAdmin = false)
    {
        using var doc = JsonDocument.Parse("{}");
        return registry.TryCreate(userId, isAdmin, "csharp", "csharp-runner", "Console.WriteLine(1);",
            80, 24, 120_000, 256, doc.RootElement);
    }

    [Fact]
    public void LocalTicketsAreSingleUseAndFreedAfterRelease()
    {
        var registry = LocalRegistry();
        var user = Guid.NewGuid();
        var first = Create(registry, user);
        Assert.NotNull(first);
        Assert.Null(Create(registry, user));
        Assert.False(registry.TryActivate(first.Value.Payload.Id, "wrong", out _));
        Assert.True(registry.TryActivate(first.Value.Payload.Id, first.Value.Ticket, out var activated));
        Assert.Equal(user, activated.UserId);
        Assert.False(registry.TryActivate(first.Value.Payload.Id, first.Value.Ticket, out _));
        registry.Release(first.Value.Payload.Id);
        Assert.NotNull(Create(registry, user));
    }

    [Fact]
    public void AdminCanOpenThreeSessionsButNotFour()
    {
        var registry = LocalRegistry();
        var user = Guid.NewGuid();
        Assert.NotNull(Create(registry, user, isAdmin: true));
        Assert.NotNull(Create(registry, user, isAdmin: true));
        Assert.NotNull(Create(registry, user, isAdmin: true));
        Assert.Null(Create(registry, user, isAdmin: true));
    }

    [Fact]
    public void MultiNodeNeverFallsBackToLocalMemory()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:MultiNode"] = "true"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new InteractiveSessionRegistry(config));
    }

    [Fact]
    public void ExplicitLocalStoreIsRejectedInMultiNodeMode()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:MultiNode"] = "true",
            ["InteractiveSessions:Store"] = "memory"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new InteractiveSessionRegistry(config));
    }
}

// The main CI exercises the in-memory and fail-closed paths. To run the optional
// real-Redis cross-replica test, set TASKFORGE_TEST_REDIS to a disposable Redis 7+
// endpoint; no Docker or special test names are required.
public sealed class InteractiveSessionRedisIntegrationTests
{
    [Fact]
    public void TicketsAndPerUserLimitsAreSharedAcrossInstances()
    {
        var connection = Environment.GetEnvironmentVariable("TASKFORGE_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(connection)) return;

        using var multiplexer = StackExchange.Redis.ConnectionMultiplexer.Connect(connection);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:MultiNode"] = "true",
            ["InteractiveSessions:Store"] = "redis"
        }).Build();
        var api1 = new InteractiveSessionRegistry(config, multiplexer);
        var api2 = new InteractiveSessionRegistry(config, multiplexer);
        var user = Guid.NewGuid();
        using var doc = JsonDocument.Parse("{}");
        var original = api1.TryCreate(user, false, "csharp", "csharp-runner", "Console.WriteLine(42);",
            80, 24, 120_000, 256, doc.RootElement);
        Assert.NotNull(original);
        try
        {
            Assert.Null(api2.TryCreate(user, false, "csharp", "csharp-runner", "Console.WriteLine(0);",
                80, 24, 120_000, 256, doc.RootElement));
            Assert.True(api2.TryActivate(original.Value.Payload.Id, original.Value.Ticket, out var session));
            Assert.Equal("Console.WriteLine(42);", session.Code);
            Assert.False(api1.TryActivate(original.Value.Payload.Id, original.Value.Ticket, out _));
        }
        finally
        {
            api1.Release(original.Value.Payload.Id);
        }
        Assert.NotNull(api2.TryCreate(user, false, "csharp", "csharp-runner", "Console.WriteLine(1);",
            80, 24, 120_000, 256, doc.RootElement));
    }
}
