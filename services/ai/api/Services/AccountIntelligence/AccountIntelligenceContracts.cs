using System.Text.Json.Serialization;

namespace TaskForge.Ai.Api.Services.AccountIntelligence;

internal sealed class SnapshotEnvelope<T>
{
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public int PeriodDays { get; set; }
    public List<T> Items { get; set; } = [];
}

internal sealed class IdentitySnapshotItem
{
    public Guid UserId { get; set; }
    public string? Login { get; set; }
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? PhoneNumber { get; set; }
    public string? ProfilePictureUrl { get; set; }
    public string? Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public long? TelegramChatId { get; set; }
    public string? TelegramUsername { get; set; }
    public DateTimeOffset? TelegramLinkedAtUtc { get; set; }
    public int TelegramLinkCount { get; set; }
    public string? Location { get; set; }
    public string? Education { get; set; }
    public string? Github { get; set; }
    public string? ProfileTelegram { get; set; }
    public string? Website { get; set; }
    public int LoginCount { get; set; }
    public int LoginDays { get; set; }
    public DateTimeOffset? LastLoginLogAt { get; set; }
    public string?[] IpHashes { get; set; } = [];
    public string?[] DeviceHashes { get; set; } = [];
    public string?[] UserAgentHashes { get; set; } = [];
}

internal sealed class EducationSnapshot
{
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public List<EducationGroupItem> Groups { get; set; } = [];
    public List<EducationMembershipItem> Memberships { get; set; } = [];
}

internal sealed class EducationGroupItem
{
    public Guid GroupId { get; set; }
    public string? Name { get; set; }
    public string? Code { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class EducationMembershipItem
{
    public Guid UserId { get; set; }
    public Guid GroupId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class TasksSnapshotItem
{
    public Guid UserId { get; set; }
    public int TotalAttempts { get; set; }
    public int PassedAttempts { get; set; }
    public int TestAttempts { get; set; }
    public int MathAttempts { get; set; }
    public int WorkSessions { get; set; }
    public int SubmissionsFromSessions { get; set; }
    public int PassedSubmissionsFromSessions { get; set; }
    public long TotalActiveDurationMs { get; set; }
    public int MaxRiskScore { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public int ActiveDays { get; set; }
    public Guid[] AssignmentIds { get; set; } = [];
    public string?[] IpHashes { get; set; } = [];
    public string?[] UserAgentHashes { get; set; } = [];
}

internal sealed class SolutionsSnapshotItem
{
    public Guid UserId { get; set; }
    public int TotalAttempts { get; set; }
    public int AcceptedAttempts { get; set; }
    public int CodeAttempts { get; set; }
    public int ImageAttempts { get; set; }
    public int DistinctAssignments { get; set; }
    public Guid[] AcceptedAssignmentIds { get; set; } = [];
    public Guid[] AssignmentIds { get; set; } = [];
    public DateTimeOffset? LastActivityAt { get; set; }
    public int ActiveDays { get; set; }
    public int TotalScore { get; set; }
    public int SolvedCount { get; set; }
    public int RatingAttempts { get; set; }
    public DateTimeOffset? LastAcceptedAt { get; set; }
}

internal sealed class ObservabilitySnapshotItem
{
    public Guid UserId { get; set; }
    public int PageViews { get; set; }
    public int SuccessfulActions { get; set; }
    public int ErrorActions { get; set; }
    public int ActiveDays { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public int DistinctPaths { get; set; }
    public string?[] IpHashes { get; set; } = [];
    public string?[] IpPrefixes { get; set; } = [];
    public string?[] UserAgentHashes { get; set; } = [];
}

internal sealed class MinecraftSnapshotEnvelope
{
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public List<MinecraftSnapshotItem> Items { get; set; } = [];
}

internal sealed class MinecraftSnapshotItem
{
    public Guid UserId { get; set; }
    public List<MinecraftLinkSnapshotItem> Links { get; set; } = [];
    public DateTimeOffset? FirstLinkedAtUtc { get; set; }
    public DateTimeOffset? LastLinkedAtUtc { get; set; }
}

internal sealed class MinecraftLinkSnapshotItem
{
    public Guid LinkId { get; set; }
    public string? PlayerName { get; set; }
    public string? PlayerUuid { get; set; }
    public DateTimeOffset LinkedAtUtc { get; set; }
}

internal sealed class AccountIntelligenceAccount
{
    public required IdentitySnapshotItem Identity { get; init; }
    public List<EducationGroupItem> Groups { get; init; } = [];
    public TasksSnapshotItem? Tasks { get; init; }
    public SolutionsSnapshotItem? Solutions { get; init; }
    public ObservabilitySnapshotItem? Observability { get; init; }
    public MinecraftSnapshotItem? Minecraft { get; init; }
    public bool Verified { get; init; }

    public Guid UserId => Identity.UserId;
    public string DisplayName => string.Join(' ', new[] { Identity.FirstName, Identity.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim() is { Length: > 0 } full
        ? full
        : Identity.Login ?? Identity.Email ?? Identity.UserId.ToString();
}

internal sealed record AccountEvidence(
    string Code,
    string Title,
    string Detail,
    int Weight,
    string Strength,
    object? Data = null);

internal sealed class AccountPairAnalysis
{
    public required AccountIntelligenceAccount A { get; init; }
    public required AccountIntelligenceAccount B { get; init; }
    public required List<AccountEvidence> Evidence { get; init; }
    public int BaseScore { get; init; }
    public int FinalScore { get; init; }
    public double Probability { get; init; }
    public Guid SuggestedPrimaryUserId { get; init; }
    public string[] SignalCodes => Evidence.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

internal sealed class AccountSuspicionAnalysis
{
    public required AccountIntelligenceAccount Account { get; init; }
    public required List<AccountEvidence> Evidence { get; init; }
    public int Score { get; init; }
}

internal sealed class AccountLearningProfile
{
    public int LabelCount { get; init; }
    public Dictionary<string, double> SignalAdjustments { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class AccountSnapshotBundle
{
    public List<AccountIntelligenceAccount> Accounts { get; init; } = [];
    public Dictionary<string, bool> Sources { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; init; } = [];
}
