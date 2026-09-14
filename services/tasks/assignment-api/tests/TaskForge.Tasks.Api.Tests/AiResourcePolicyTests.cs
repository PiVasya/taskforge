using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using TaskForge.Tasks.Api.Services.Common;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class AiResourcePolicyTests
{
    [Fact]
    public void Defaults_AreUnlimitedOnlyForAiAccounts()
    {
        var ai = Context("ai");
        var human = Context("human");
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

        Assert.True(AssignmentApiCommonService.HasUnlimitedAiTaskEnergy(ai, config));
        Assert.True(AssignmentApiCommonService.HasUnlimitedAiTaskRateLimit(ai, config));
        Assert.True(AssignmentApiCommonService.HasUnlimitedAiTaskAttempts(ai, config));
        Assert.True(AssignmentApiCommonService.IgnoreAiTaskAttemptTimeLimits(ai, config));
        Assert.False(AssignmentApiCommonService.HasUnlimitedAiTaskEnergy(human, config));
        Assert.False(AssignmentApiCommonService.HasUnlimitedAiTaskRateLimit(human, config));
        Assert.False(AssignmentApiCommonService.HasUnlimitedAiTaskAttempts(human, config));
        Assert.False(AssignmentApiCommonService.IgnoreAiTaskAttemptTimeLimits(human, config));
    }

    [Fact]
    public void ExplicitConfiguration_CanDisableAiUnlimitedPolicy()
    {
        var ai = Context("ai");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiAccounts:UnlimitedTaskEnergy"] = "false",
            ["AiAccounts:UnlimitedTaskRateLimit"] = "false",
            ["AiAccounts:UnlimitedTaskAttempts"] = "false",
            ["AiAccounts:IgnoreTaskAttemptTimeLimits"] = "false",
        }).Build();

        Assert.False(AssignmentApiCommonService.HasUnlimitedAiTaskEnergy(ai, config));
        Assert.False(AssignmentApiCommonService.HasUnlimitedAiTaskRateLimit(ai, config));
        Assert.False(AssignmentApiCommonService.HasUnlimitedAiTaskAttempts(ai, config));
        Assert.False(AssignmentApiCommonService.IgnoreAiTaskAttemptTimeLimits(ai, config));
    }

    private static DefaultHttpContext Context(string accountType)
        => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("account_type", accountType)],
                authenticationType: "contract-test"))
        };
}
