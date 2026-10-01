using TaskForge.Tasks.Api.Services.Access;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class RequestSecurityTests
{
    [Fact]
    public void ClassifiesCourseRoutesByActualAccessRequirement()
    {
        var id = Guid.NewGuid();

        Assert.Equal(TaskForgeRequestSecurity.Requirement.Authenticated,
            TaskForgeRequestSecurity.GetRequirement("tasks", "POST", $"/api/courses/{id:D}/learning-map/delta"));
        Assert.Equal(TaskForgeRequestSecurity.Requirement.Editor,
            TaskForgeRequestSecurity.GetRequirement("tasks", "POST", $"/api/courses/{id:D}"));
        Assert.Equal(TaskForgeRequestSecurity.Requirement.Authenticated,
            TaskForgeRequestSecurity.GetRequirement("tasks", "GET", $"/api/courses/{id:D}"));
        Assert.Equal(TaskForgeRequestSecurity.Requirement.Internal,
            TaskForgeRequestSecurity.GetRequirement("tasks", "GET", "/api/internal/courses/x"));
        Assert.Equal(TaskForgeRequestSecurity.Requirement.Editor,
            TaskForgeRequestSecurity.GetRequirement("tasks", "GET", $"/api/assignments/{id:D}/edit-meta"));
        Assert.Equal(TaskForgeRequestSecurity.Requirement.Editor,
            TaskForgeRequestSecurity.GetRequirement("tasks", "GET", $"/api/code-assignments/{id:D}/edit"));
        Assert.Equal(TaskForgeRequestSecurity.Requirement.Editor,
            TaskForgeRequestSecurity.GetRequirement("tasks", "PUT", $"/api/test-assignments/{id:D}"));
        Assert.Equal(TaskForgeRequestSecurity.Requirement.Editor,
            TaskForgeRequestSecurity.GetRequirement("tasks", "GET", $"/api/sql-assignments/{id:D}/edit"));
        Assert.Equal(TaskForgeRequestSecurity.Requirement.Editor,
            TaskForgeRequestSecurity.GetRequirement("tasks", "POST", $"/api/sql-assignments/{id:D}/spec/publish"));
        Assert.Equal(TaskForgeRequestSecurity.Requirement.Internal,
            TaskForgeRequestSecurity.GetRequirement("tasks", "POST", $"/api/internal/image-assignments/{id:D}/evaluate-solution"));
        Assert.Equal(TaskForgeRequestSecurity.Requirement.Authenticated,
            TaskForgeRequestSecurity.GetRequirement("tasks", "POST", $"/api/test-assignments/{id:D}/attempts"));
    }
}
