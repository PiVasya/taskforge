using Microsoft.AspNetCore.Http;
using TaskForge.Ai.Api.Services.Mapping;
using Xunit;

namespace TaskForge.Ai.Api.Tests;

public sealed class ForwardCookieAuthTests
{
    [Fact]
    public void CookieDelegation_PreservesCsrfProofWithoutInventingBearer()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "tf_at=access-cookie";
        context.Request.Headers["X-TaskForge-CSRF"] = "1";
        context.Request.Headers.Origin = "https://taskforge.test";
        using var client = new HttpClient();
        AiApiMappingService.ForwardAuth(context.Request, client);
        Assert.Equal("tf_at=access-cookie", Assert.Single(client.DefaultRequestHeaders.GetValues("Cookie")));
        Assert.Equal("1", Assert.Single(client.DefaultRequestHeaders.GetValues("X-TaskForge-CSRF")));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        // The original browser origin was checked at the edge; the delegated
        // request targets a different internal service origin.
        Assert.False(client.DefaultRequestHeaders.Contains("Origin"));
    }

    [Fact]
    public void ExplicitMachineCredential_IsPreservedWithoutAmbientCookies()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer machine-credential";
        using var client = new HttpClient();
        AiApiMappingService.ForwardAuth(context.Request, client);
        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("machine-credential", client.DefaultRequestHeaders.Authorization?.Parameter);
        Assert.False(client.DefaultRequestHeaders.Contains("Cookie"));
        Assert.False(client.DefaultRequestHeaders.Contains("X-TaskForge-CSRF"));
    }
}
