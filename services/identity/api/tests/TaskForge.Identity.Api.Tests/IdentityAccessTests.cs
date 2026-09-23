using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using TaskForge.Identity.Api.Domain;
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
