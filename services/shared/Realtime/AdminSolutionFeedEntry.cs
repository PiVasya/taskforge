using Microsoft.EntityFrameworkCore;

namespace TaskForge.Realtime;

/// <summary>
/// An append-only event committed in the SAME YugabyteDB transaction as the owning
/// solution/attempt. Each owner has its own table; no cross-service database writes.
/// This is not a cache and is never published through Redis Pub/Sub.
/// </summary>
public sealed class AdminSolutionFeedEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = string.Empty;
    public Guid ItemId { get; set; }
    public Guid UserId { get; set; }
    public Guid AssignmentId { get; set; }
    public string? Status { get; set; }
    public int? Score { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public AdminSolutionEvent ToWireEvent() => new(
        Id.ToString("N"), Kind, ItemId, UserId, AssignmentId, Status, Score, OccurredAtUtc);

    public static void Configure(ModelBuilder model)
    {
        model.Entity<AdminSolutionFeedEntry>(entity =>
        {
            entity.ToTable("AdminSolutionFeedEvents");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OccurredAtUtc, x.Id });
            entity.HasIndex(x => new { x.Kind, x.ItemId, x.OccurredAtUtc });
            entity.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40);
        });
    }
}

public sealed record AdminSolutionEvent(
    string EventId, string Kind, Guid ItemId, Guid UserId, Guid AssignmentId,
    string? Status, int? Score, DateTimeOffset OccurredAtUtc);
