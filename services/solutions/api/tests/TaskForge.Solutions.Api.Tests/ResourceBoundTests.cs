using System.Text.Json;
using TaskForge.Solutions.Api.Contracts;
using TaskForge.Solutions.Api.Services.Mapping;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class ResourceBoundTests
{
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    [Fact]
    public void SlidingWindow_RejectsExcessForExistingIdentity()
    {
        var clock = new MutableClock();
        var limiter = new SlidingWindowRateLimiter(maxKeys: 2, time: clock);
        Assert.True(limiter.Allow("user-1", 2, TimeSpan.FromMinutes(5)));
        Assert.True(limiter.Allow("user-1", 2, TimeSpan.FromMinutes(5)));
        Assert.False(limiter.Allow("user-1", 2, TimeSpan.FromMinutes(5)));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(limiter.Allow("user-1", 2, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void SlidingWindow_AtCapacityRejectsUnknownIdentitiesUntilExpired()
    {
        var clock = new MutableClock();
        var limiter = new SlidingWindowRateLimiter(maxKeys: 2, time: clock);
        Assert.True(limiter.Allow("long-lived", 1, TimeSpan.FromMinutes(5)));
        Assert.True(limiter.Allow("short-lived", 1, TimeSpan.FromMinutes(1)));
        Assert.False(limiter.Allow("new-user", 1, TimeSpan.FromMinutes(1)));
        clock.Advance(TimeSpan.FromSeconds(70));
        // A full-table sweep must use each identity's own window.
        Assert.True(limiter.Allow("new-user", 1, TimeSpan.FromMinutes(1)));
        Assert.False(limiter.Allow("long-lived", 1, TimeSpan.FromMinutes(5)));
        Assert.False(limiter.Allow("yet-another", 1, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void SlidingWindow_ConcurrentCallsCannotExceedLimit()
    {
        var limiter = new SlidingWindowRateLimiter();
        var admitted = 0;
        Parallel.For(0, 500, _ =>
        {
            if (limiter.Allow("same-user", 7, TimeSpan.FromMinutes(1)))
                Interlocked.Increment(ref admitted);
        });
        Assert.Equal(7, admitted);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void SlidingWindow_RejectsInvalidIdentity(string? key)
    {
        var limiter = new SlidingWindowRateLimiter();
        Assert.False(limiter.Allow(key!, 10, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void LegacyJsonCaseCountingDoesNotNeedToCloneTheFullResponse()
    {
        var json = JsonSerializer.Serialize(new
        {
            cases = new[] { new { passed = true, actualOutput = new string('H', 256_000) },
                new { passed = false, actualOutput = new string('X', 256_000) } }
        });
        var counts = SolutionsApiMappingService.CountCasesFromRawJson(json);
        Assert.Equal(1, counts.passed);
        Assert.Equal(1, counts.failed);
        Assert.Equal(2, counts.total);
    }
}

public sealed class BoundedAdminSearchTests
{
    [Theory]
    [InlineData("hello", "hello", 0)]
    [InlineData("hello", "hella", 1)]
    [InlineData("hello", "help", 2)]
    [InlineData("algorithm", "algo", 5)]
    public void FuzzyDistanceStopsWhenDifferenceExceedsFour(string original, string query, int expected)
    {
        var distance = TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService.Levenshtein(original, query, 4);
        Assert.Equal(expected, distance);
    }
}
