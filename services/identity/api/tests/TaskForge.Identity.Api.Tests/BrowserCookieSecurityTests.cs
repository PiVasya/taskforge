using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskForge.Identity.Api.Domain;
using TaskForge.Identity.Api.Services.Access;
using TaskForge.Identity.Api.Services.Common;
using Xunit;

namespace TaskForge.Identity.Api.Tests;

public sealed class BrowserCookieSecurityTests
{
    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:Key"] = "cookie-security-test-key-0123456789-abcdefghijklmnopqrstuvwxyz-ABCDEFGHIJKLMN",
        ["Jwt:Issuer"] = "CookieTests", ["Jwt:Audience"] = "CookieTests"
    }).Build();

    private static DefaultHttpContext Request(string method = "POST", string path = "/api/profile", bool cookie = true, bool header = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("taskforge.test");
        context.Request.Method = method;
        context.Request.Path = path;
        if (cookie) context.Request.Headers.Cookie = "tf_at=credential";
        if (header) context.Request.Headers["X-TaskForge-CSRF"] = "1";
        return context;
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void CookieWrites_RequireNonSimpleHeader(string method)
    {
        Assert.True(TaskForgeRequestSecurity.IsBrowserRequestAllowed(Request(method).Request));
        Assert.False(TaskForgeRequestSecurity.IsBrowserRequestAllowed(Request(method, header: false).Request));
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/register")]
    [InlineData("/api/auth/refresh")]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/remember")]
    [InlineData("/api/auth/password-recovery/reset")]
    public void IdentityWrites_AreProtectedEvenWithoutAnExistingCookie(string path)
    {
        Assert.False(TaskForgeRequestSecurity.IsBrowserRequestAllowed(Request(path: path, cookie: false, header: false).Request));
        Assert.True(TaskForgeRequestSecurity.IsBrowserRequestAllowed(Request(path: path, cookie: false).Request));
    }

    [Theory]
    [InlineData("https://attacker.test")]
    [InlineData("https://untrusted.taskforge.test")]
    [InlineData("https://taskforge.test.attacker.test")]
    [InlineData("http://taskforge.test")]
    [InlineData("https://taskforge.test:8443")]
    [InlineData("null")]
    public void ForeignOrigins_AreRejectedEvenWithCustomHeader(string origin)
    {
        var context = Request();
        context.Request.Headers.Origin = origin;
        Assert.False(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
    }

    [Fact]
    public void SameOrigin_UsesGatewaySchemeAndPreservedHost()
    {
        var context = Request();
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers.Origin = "https://taskforge.test";
        Assert.True(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
        context.Request.Headers["Sec-Fetch-Site"] = "same-site";
        Assert.False(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
    }

    [Fact]
    public void BrowserWebSocket_UsesOriginWithoutCustomHeaders()
    {
        var context = Request("GET", "/hubs/support", header: false);
        context.Request.Headers["Upgrade"] = "websocket";
        context.Request.Headers.Origin = "https://taskforge.test";
        Assert.True(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
        context.Request.Headers.Origin = "https://other.taskforge.test";
        Assert.False(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
        context.Request.Headers.Remove("Origin");
        Assert.False(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        Assert.True(TaskForgeRequestSecurity.IsBrowserRequestAllowed(context.Request));
    }

    [Fact]
    public void SafeProfileRead_DoesNotRequireCustomHeaders()
        => Assert.True(TaskForgeRequestSecurity.IsBrowserRequestAllowed(Request("GET", header: false).Request));

    [Fact]
    public void ExplicitNonBrowserBearer_DoesNotDependOnAmbientCookies()
        => Assert.True(TaskForgeRequestSecurity.IsBrowserRequestAllowed(Request(cookie: false, header: false).Request));

    [Fact]
    public async Task MiddlewareRejectsForgedPublicLogoutBeforeExecutingHandler()
    {
        using var services = new ServiceCollection().AddSingleton(Config()).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        var called = false;
        app.UseTaskForgeRequestSecurity("identity");
        app.Run(_ => { called = true; return Task.CompletedTask; });
        var context = Request(path: "/api/auth/logout", header: false);
        context.RequestServices = services;
        context.Response.Body = new MemoryStream();
        await app.Build()(context);
        Assert.False(called);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Empty(context.Response.Headers.SetCookie);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrowserSessionJson_HasMetadataButNoCredential(bool remember)
    {
        var context = Request();
        var user = new IdentityUser { Id = Guid.NewGuid(), Login = "cookie-user", Role = "User" };
        var access = IdentityApiCommonService.IssueLoginSession(context, user, Config(), ["User"], remember);
        var data = IdentityApiCommonService.BrowserSessionInfo(access, new { id = user.Id, roles = new[] { "User" } }, remember);
        var json = JsonSerializer.Serialize(data);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(remember, document.RootElement.GetProperty("rememberMe").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("accessToken", out _));
        Assert.False(document.RootElement.TryGetProperty("refreshToken", out _));
        Assert.DoesNotContain(access, json);
        Assert.Equal(new JwtSecurityTokenHandler().ReadJwtToken(access).ValidTo, document.RootElement.GetProperty("expiresAtUtc").GetDateTimeOffset().UtcDateTime);
    }

    [Fact]
    public void AccessAuthentication_RejectsRefreshAndQueryTokens()
    {
        var user = new IdentityUser { Id = Guid.NewGuid(), Login = "cookie-user", Role = "User" };
        var config = Config();
        var access = IdentityApiAccessService.CreateJwt(user, config, TimeSpan.FromMinutes(5), "access");
        var refresh = IdentityApiAccessService.CreateJwt(user, config, TimeSpan.FromDays(7), "refresh");
        var context = Request(cookie: false);
        context.Request.Headers.Cookie = $"tf_at={access}";
        Assert.NotNull(TaskForgeRequestSecurity.ValidateUser(context, config));
        context.Request.Headers.Cookie = $"tf_at={refresh}";
        Assert.Null(TaskForgeRequestSecurity.ValidateUser(context, config));
        context.Request.Headers.Remove("Cookie");
        context.Request.Headers.Authorization = $"Bearer {refresh}";
        Assert.Null(TaskForgeRequestSecurity.ValidateUser(context, config));
        context.Request.Headers.Authorization = $"Bearer {access}";
        Assert.NotNull(TaskForgeRequestSecurity.ValidateUser(context, config));
        context.Request.Headers.Remove("Authorization");
        context.Request.Path = "/hubs/support";
        context.Request.QueryString = new QueryString($"?access_token={access}");
        Assert.Null(TaskForgeRequestSecurity.ValidateUser(context, config));
    }

    [Fact]
    public void Logout_DeletesBothCredentialCookiesWithoutReturningCredentials()
    {
        var context = Request();
        IdentityApiCommonService.ClearAuthCookies(context);
        var cookies = context.Response.Headers.SetCookie.Select(value => value ?? string.Empty).ToArray();
        Assert.Contains(cookies, value => value.StartsWith("tf_at=;", StringComparison.Ordinal));
        Assert.Contains(cookies, value => value.StartsWith("tf_rt=;", StringComparison.Ordinal));
        Assert.All(cookies, value => Assert.Contains("httponly", value.ToLowerInvariant()));
        Assert.All(cookies, value => Assert.Contains("secure", value.ToLowerInvariant()));
    }
}
