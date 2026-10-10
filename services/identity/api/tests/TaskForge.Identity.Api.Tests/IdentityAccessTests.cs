using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using TaskForge.Identity.Api.Domain;
using TaskForge.Identity.Api.Contracts;
using TaskForge.Identity.Api.Services.Common;
using TaskForge.Identity.Api.Services.Access;
using TaskForge.Identity.Api.Services.Image;
using Xunit;

namespace TaskForge.Identity.Api.Tests;

public sealed class IdentityAccessTests
{
    private static IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "contract-test-signing-key-contract-test-signing-key-123456789",
            ["Jwt:Issuer"] = "TaskForge.ContractTests",
            ["Jwt:Audience"] = "TaskForge.ContractTests",
            ["Bootstrap:AdminEmails"] = "admin@example.test",
            ["Bootstrap:FirstUserIsAdmin"] = "false",
        })
        .Build();

    [Fact]
    public void LoginRequest_DoesNotEnableRememberMeUnlessExplicitlyRequested()
    {
        var ordinary = JsonSerializer.Deserialize<LoginRequest>("""{"login":"test","password":"pass"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var remembered = JsonSerializer.Deserialize<LoginRequest>("""{"login":"test","password":"pass","rememberMe":true}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(ordinary);
        Assert.False(ordinary.RememberMe);
        Assert.NotNull(remembered);
        Assert.True(remembered.RememberMe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoginSession_RefreshJwtIsOnlyCreatedWhenDeviceOptedIn(bool rememberMe)
    {
        var user = new IdentityUser { Id = Guid.NewGuid(), Login = "test", Role = "User", AccountType = "human" };
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        var access = IdentityApiCommonService.IssueLoginSession(context, user, BuildConfig(), ["User"], rememberMe);
        var cookies = context.Response.Headers["Set-Cookie"].Select(value => value ?? string.Empty).ToArray();
        var accessCookie = Assert.Single(cookies.Where(value => value.StartsWith("tf_at=", StringComparison.Ordinal)));
        Assert.Contains(access, accessCookie);
        Assert.True(accessCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.True(accessCookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(access);
        Assert.Equal("access", jwt.Claims.Single(c => c.Type == "token_type").Value);

        var refreshCookie = Assert.Single(cookies.Where(value => value.StartsWith("tf_rt=", StringComparison.Ordinal)));
        if (rememberMe)
        {
            var token = refreshCookie.Split(';')[0]["tf_rt=".Length..];
            var refresh = new JwtSecurityTokenHandler().ReadJwtToken(token);
            Assert.Equal("refresh", refresh.Claims.Single(c => c.Type == "token_type").Value);
            Assert.True(refresh.ValidTo > DateTime.UtcNow.AddDays(6));
            Assert.True(refreshCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
            Assert.True(refreshCookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // Only an expired deletion cookie is sent, never a signed refresh JWT.
            Assert.True(refreshCookie.StartsWith("tf_rt=;", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DisablingRememberMe_ClearsRefreshOnly_NotTheAccessCookie()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        IdentityApiCommonService.SetAuthCookies(context, "access-jwt", "refresh-jwt", TimeSpan.FromHours(2), TimeSpan.FromDays(7));
        IdentityApiCommonService.ClearRefreshCookie(context);
        var cookies = context.Response.Headers["Set-Cookie"].ToArray();
        Assert.Contains(cookies, cookie => cookie!.StartsWith("tf_at=access-jwt", StringComparison.Ordinal));
        Assert.True(cookies.Last()!.StartsWith("tf_rt=;", StringComparison.Ordinal));
    }

    [Fact]
    public void Jwt_ContainsNormalizedAccountTypeAndFeatureRoles()
    {
        var user = new IdentityUser
        {
            Id = Guid.NewGuid(),
            Login = "bot",
            Email = "bot@example.test",
            Role = "User",
            AccountType = "AI"
        };

        var token = IdentityApiAccessService.CreateJwt(user, BuildConfig(), TimeSpan.FromMinutes(5), "access", ["Minecraft"]);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal("ai", jwt.Claims.Single(x => x.Type == "account_type").Value);
        Assert.Contains(jwt.Claims, x => x.Type == "roles" && x.Value.Contains("Minecraft", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BootstrapRolePolicy_DoesNotSilentlyPromoteFirstUser()
    {
        var config = BuildConfig();

        Assert.Equal("Admin", IdentityApiAccessService.ResolveInitialRole("admin@example.test", false, config));
        Assert.Equal("User", IdentityApiAccessService.ResolveInitialRole("first@example.test", true, config));
    }


    [Fact]
    public void SuperAdminJwt_InheritsAdminRoleForExistingAdminAuthorization()
    {
        var user = new IdentityUser
        {
            Id = Guid.NewGuid(),
            Login = "root",
            Email = "root@example.test",
            Role = "SuperAdmin",
            AccountType = "human"
        };

        var token = IdentityApiAccessService.CreateJwt(user, BuildConfig(), TimeSpan.FromMinutes(5), "access");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var roleValues = jwt.Claims.Where(x => x.Type == "role" || x.Type.EndsWith("/role", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).ToArray();

        Assert.Contains("SuperAdmin", roleValues, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Admin", roleValues, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(jwt.Claims, x => x.Type == "primary_role" && x.Value == "SuperAdmin");
    }

    [Fact]
    public void RoleHierarchy_AllowsOnlyStrictlyLowerAssignableRoles()
    {
        var superAdmin = new FeatureRole { Code = "SuperAdmin", Rank = IdentityApiAccessService.SuperAdminRoleRank, IsAssignable = false, IsActive = true };
        var admin = new FeatureRole { Code = "Admin", Rank = IdentityApiAccessService.AdminRoleRank, IsAssignable = true, IsActive = true };
        var editor = new FeatureRole { Code = "Editor", Rank = IdentityApiAccessService.EditorRoleRank, IsAssignable = true, IsActive = true };

        Assert.False(IdentityApiAccessService.CanManageRole(admin, admin));
        Assert.False(IdentityApiAccessService.CanManageRole(admin, superAdmin));
        Assert.True(IdentityApiAccessService.CanManageRole(admin, editor));
        Assert.True(IdentityApiAccessService.CanManageRole(superAdmin, admin));
        Assert.False(IdentityApiAccessService.CanManageRole(superAdmin, superAdmin));

        Assert.False(IdentityApiAccessService.CanChangePrimaryRole(admin, admin, editor));
        Assert.True(IdentityApiAccessService.CanChangePrimaryRole(superAdmin, admin, editor));
        Assert.False(IdentityApiAccessService.CanChangePrimaryRole(superAdmin, superAdmin, admin));
    }

    [Fact]
    public void PasswordHash_VerifiesOnlyTheCorrectPasswordAndNeedsNoImmediateRehash()
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var hash = IdentityApiAccessService.HashPassword("correct horse battery staple", salt);

        Assert.True(IdentityApiAccessService.VerifyPassword("correct horse battery staple", salt, hash));
        Assert.False(IdentityApiAccessService.VerifyPassword("wrong", salt, hash));
        Assert.False(IdentityApiAccessService.NeedsPasswordRehash(hash));
    }
    [Fact]
    public void PublicProfileExtra_LegacyPayloadShowsStatsButKeepsOptionalPersonalFieldsPrivate()
    {
        var extra = IdentityApiImageService.ReadPublicProfileExtra("""{"bio":"Bio","location":"Minsk","links":{"github":"github.com/example"},"skills":["C#"]}""");

        Assert.True(extra.PublicProfileEnabled);
        Assert.True(extra.ShowStats);
        Assert.False(extra.ShowBio);
        Assert.False(extra.ShowLocation);
        Assert.False(extra.ShowGithub);
        Assert.False(extra.ShowSkills);
    }

    [Fact]
    public void PublicProfileExtra_ExplicitStatsOptOutIsPreserved()
    {
        var extra = IdentityApiImageService.ReadPublicProfileExtra("""{"showStats":false}""");

        Assert.False(extra.ShowStats);
    }

}
