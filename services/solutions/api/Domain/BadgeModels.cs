namespace TaskForge.Solutions.Api.Domain;

public sealed class Badge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class UserBadge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid BadgeId { get; set; }
    public DateTimeOffset AwardedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class UserQuotaBucket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string BucketType { get; set; } = "tasks";
    public int Tokens { get; set; }
    public DateTimeOffset LastRefillAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class UserImageTaskSolution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid AssignmentId { get; set; }
    public string Language { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int SimilarityPercent { get; set; }
    public bool Passed { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
