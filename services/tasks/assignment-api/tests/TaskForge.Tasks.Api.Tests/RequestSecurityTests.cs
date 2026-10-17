using Microsoft.AspNetCore.Http;
using TaskForge.Tasks.Api.Services.Access;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class RequestSecurityTests
{
    private static DefaultHttpContext BrowserRequest(string method, string path)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("taskforge.test");
        http.Request.Headers.Cookie = "tf_at=credential";
        return http;
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void CookieMutationsNeedCsrfHeader(string method)
    {
        var context = BrowserRequest(method, "/api/courses");
        Assert.False(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
        context.Request.Headers["X-TaskForge-CSRF"] = "1";
        Assert.True(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
        context.Request.Headers.Origin = "https://attacker.test";
        Assert.False(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
    }

    [Fact]
    public void BearerOnlyMachineRequestsDoNotRequireBrowserCsrfHeader()
    {
        var context = BrowserRequest("POST", "/api/courses");
        context.Request.Headers.Remove("Cookie");
        context.Request.Headers.Authorization = "Bearer machine-access-token";
        Assert.True(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
    }

    [Fact]
    public void CookieWebSocketsRequireTrustedOriginOrFetchMetadata()
    {
        var context = BrowserRequest("GET", "/hubs/assignments");
        context.Request.Headers.Upgrade = "websocket";
        Assert.False(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
        context.Request.Headers.Origin = "https://taskforge.test";
        Assert.True(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
        context.Request.Headers.Origin = "https://untrusted.taskforge.test";
        Assert.False(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
    }

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
