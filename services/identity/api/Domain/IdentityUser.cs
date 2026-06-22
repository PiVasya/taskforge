namespace TaskForge.Identity.Api.Domain;

public sealed class IdentityUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Login { get; set; }
    public string? Email { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "User";
    public string? PhoneNumber { get; set; }
    public string? ProfilePictureUrl { get; set; }
    public string? AdditionalDataJson { get; set; }
    public long? TelegramChatId { get; set; }
    public string? TelegramUsername { get; set; }
    public DateTimeOffset? TelegramLinkedAtUtc { get; set; }
    public int TelegramLinkCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}
