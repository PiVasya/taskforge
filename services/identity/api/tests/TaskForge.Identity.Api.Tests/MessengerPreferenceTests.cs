using TaskForge.Identity.Api.Domain;
using TaskForge.Identity.Api.Endpoints;
using Xunit;

namespace TaskForge.Identity.Api.Tests;

public sealed class MessengerPreferenceTests
{
    [Fact]
    public void NoMessengerLinked_HasNoPrimaryMessenger()
    {
        var user = new IdentityUser { PrimaryMessenger = "discord" };
        Assert.Null(IdentityApiEndpoints.EffectivePrimaryMessenger(user));
    }

    [Fact]
    public void TelegramOnly_DefaultsToTelegram()
    {
        var user = new IdentityUser { TelegramChatId = 12345 };
        Assert.Equal("telegram", IdentityApiEndpoints.EffectivePrimaryMessenger(user));
    }

    [Fact]
    public void DiscordOnly_DefaultsToDiscord()
    {
        var user = new IdentityUser { DiscordUserId = "discord-user" };
        Assert.Equal("discord", IdentityApiEndpoints.EffectivePrimaryMessenger(user));
    }

    [Theory]
    [InlineData("telegram")]
    [InlineData("discord")]
    public void BothLinked_HonorsSelectedPrimaryMessenger(string primary)
    {
        var user = new IdentityUser { TelegramChatId = 12345, DiscordUserId = "discord-user", PrimaryMessenger = primary };
        Assert.Equal(primary, IdentityApiEndpoints.EffectivePrimaryMessenger(user));
    }

    [Fact]
    public void StalePrimaryMessenger_FallsBackToRemainingLink()
    {
        var user = new IdentityUser { DiscordUserId = "discord-user", PrimaryMessenger = "telegram" };
        Assert.Equal("discord", IdentityApiEndpoints.EffectivePrimaryMessenger(user));
    }

    [Fact]
    public void UnlinkingPrimaryDiscord_SwitchesToTelegram()
    {
        var user = new IdentityUser
        {
            TelegramChatId = 12345,
            DiscordUserId = "discord-user",
            DiscordName = "Name",
            DiscordLinkedAtUtc = DateTimeOffset.UtcNow,
            PrimaryMessenger = "discord"
        };
        IdentityApiEndpoints.ClearDiscordLink(user);
        Assert.Null(user.DiscordUserId);
        Assert.Null(user.DiscordName);
        Assert.Null(user.DiscordLinkedAtUtc);
        Assert.Equal("telegram", IdentityApiEndpoints.EffectivePrimaryMessenger(user));
    }

    [Fact]
    public void UnlinkingLastMessenger_DisablesMessengerAccess()
    {
        var user = new IdentityUser { DiscordUserId = "discord-user", PrimaryMessenger = "discord" };
        IdentityApiEndpoints.ClearDiscordLink(user);
        Assert.Null(IdentityApiEndpoints.EffectivePrimaryMessenger(user));
    }
}
