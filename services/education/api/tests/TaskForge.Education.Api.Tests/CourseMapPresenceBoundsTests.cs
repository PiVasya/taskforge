using Microsoft.Extensions.DependencyInjection;
using TaskForge.Education.Api.Services.CourseMaps;
using Xunit;

namespace TaskForge.Education.Api.Tests;

public sealed class CourseMapPresenceBoundsTests
{
    [Fact]
    public async Task RedisFallbackBoundsConnectionsPerRoomWithoutDroppingExistingOnes()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = new CourseMapPresenceStore(services);
        var course = Guid.NewGuid();
        var first = Guid.NewGuid();
        await store.UpsertAsync(course, "first-connection", first, "First", null, false);

        for (var i = 0; i < 300; i++)
            await store.UpsertAsync(course, $"connection-{i}", Guid.NewGuid(), "Learner", null, false);

        var members = await store.GetAsync(course);
        Assert.Equal(256, members.Count);
        Assert.Contains(members, x => x.UserId == first);

        await store.UpsertAsync(course, "first-connection", first, "Updated", null, true);
        members = await store.GetAsync(course);
        Assert.Contains(members, x => x.UserId == first && x.DisplayName == "Updated" && x.IsDirty);
    }

    [Fact]
    public async Task RedisFallbackRemoveDoesNotLeaveAnEmptyRoomVisible()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = new CourseMapPresenceStore(services);
        var course = Guid.NewGuid();
        await store.UpsertAsync(course, "connection", Guid.NewGuid(), "Learner", null, false);
        Assert.Single(await store.GetAsync(course));
        Assert.Empty(await store.RemoveAsync(course, "connection"));
        Assert.Empty(await store.GetAsync(course));
    }
}
