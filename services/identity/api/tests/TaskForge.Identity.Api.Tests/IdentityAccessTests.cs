using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using TaskForge.Identity.Api.Domain;
using TaskForge.Identity.Api.Services.Access;
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
    public void PasswordHash_VerifiesOnlyTheCorrectPasswordAndNeedsNoImmediateRehash()
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var hash = IdentityApiAccessService.HashPassword("correct horse battery staple", salt);

        Assert.True(IdentityApiAccessService.VerifyPassword("correct horse battery staple", salt, hash));
        Assert.False(IdentityApiAccessService.VerifyPassword("wrong", salt, hash));
        Assert.False(IdentityApiAccessService.NeedsPasswordRehash(hash));
    }
}
