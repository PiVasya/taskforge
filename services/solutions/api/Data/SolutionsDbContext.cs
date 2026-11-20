using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TaskForge.Solutions.Api.Domain;
using TaskForge.Realtime;

namespace TaskForge.Solutions.Api.Data;

public sealed class SolutionsDbContext(DbContextOptions<SolutionsDbContext> options) : DbContext(options)
{
    public DbSet<AdminSolutionFeedEntry> AdminSolutionFeedEvents => Set<AdminSolutionFeedEntry>();
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<UserRating> UserRatings => Set<UserRating>();
    public DbSet<LeaderboardEntry> LeaderboardEntries => Set<LeaderboardEntry>();
    public DbSet<RatingAcceptedActivity> RatingAcceptedActivities => Set<RatingAcceptedActivity>();
    public DbSet<RatingDirtyUser> RatingDirtyUsers => Set<RatingDirtyUser>();
    public DbSet<RatingProjectionCheckpoint> RatingProjectionCheckpoints => Set<RatingProjectionCheckpoint>();
    public DbSet<SolutionSubmission> Submissions => Set<SolutionSubmission>();
    public DbSet<TaskAttempt> TaskAttempts => Set<TaskAttempt>();
    public DbSet<Badge> Badges => Set<Badge>();
    public DbSet<UserBadge> UserBadges => Set<UserBadge>();
    public DbSet<UserQuotaBucket> UserQuotaBuckets => Set<UserQuotaBucket>();
    public DbSet<UserImageTaskSolution> ImageSolutions => Set<UserImageTaskSolution>();

    // A state change and its feed event are committed by ONE SaveChanges transaction.
    // Do not call external publishers after COMMIT: a process crash could drop the event.
    internal void CaptureAdminSolutionEvents()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<SolutionSubmission>().ToArray())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            var row = entry.Entity;
            if (!row.UserId.HasValue || row.UserId == Guid.Empty || row.AssignmentId == Guid.Empty) continue;
            if (entry.State == EntityState.Modified &&
                !entry.Property(x => x.Status).IsModified &&
                !entry.Property(x => x.Score).IsModified &&
                !entry.Property(x => x.ResultJson).IsModified &&
                !entry.Property(x => x.UserId).IsModified) continue;
            var kind = TaskForge.Solutions.Api.Services.Results.OwnershipDeletionEvents.SubmissionKind(row);
            if (ChangeTracker.Entries<AdminSolutionFeedEntry>().Any(e => e.State == EntityState.Added &&
                e.Entity.ItemId == row.Id && e.Entity.Kind == kind && e.Entity.UserId == row.UserId &&
                e.Entity.Status == row.Status && e.Entity.Score == row.Score)) continue;
            AdminSolutionFeedEvents.Add(new AdminSolutionFeedEntry
            {
                Kind = kind,
                ItemId = row.Id, UserId = row.UserId.Value, AssignmentId = row.AssignmentId,
                Status = row.Status, Score = row.Score
            });
        }
        foreach (var entry in ChangeTracker.Entries<UserImageTaskSolution>().ToArray())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            var row = entry.Entity;
            if (row.UserId == Guid.Empty || row.AssignmentId == Guid.Empty) continue;
            if (entry.State == EntityState.Modified &&
                !entry.Property(x => x.Passed).IsModified &&
                !entry.Property(x => x.SimilarityPercent).IsModified &&
                !entry.Property(x => x.UserId).IsModified) continue;
            var imageStatus = row.Passed ? "Accepted" : "Rejected";
            if (ChangeTracker.Entries<AdminSolutionFeedEntry>().Any(e => e.State == EntityState.Added &&
                e.Entity.ItemId == row.Id && e.Entity.Kind == "image" && e.Entity.UserId == row.UserId &&
                e.Entity.Status == imageStatus && e.Entity.Score == row.SimilarityPercent)) continue;
            AdminSolutionFeedEvents.Add(new AdminSolutionFeedEntry
            {
                Kind = "image", ItemId = row.Id, UserId = row.UserId, AssignmentId = row.AssignmentId,
                Status = row.Passed ? "Accepted" : "Rejected", Score = row.SimilarityPercent
            });
        }
    }

    internal void CaptureInteractiveAttemptEvents()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<TaskAttempt>().ToArray())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            var row = entry.Entity;
            if (!row.SubmittedAt.HasValue || row.UserId == Guid.Empty || row.TaskAssignmentId == Guid.Empty) continue;
            if (row.Kind is not ("test" or "math")) continue;
            if (entry.State == EntityState.Modified &&
                !entry.Property(x => x.SubmittedAt).IsModified &&
                !entry.Property(x => x.ScorePercent).IsModified &&
                !entry.Property(x => x.Passed).IsModified &&
                !entry.Property(x => x.UserId).IsModified) continue;
            var status = entry.State == EntityState.Deleted ? "Deleted" : row.Passed ? "Accepted" : "Rejected";
            var score = entry.State == EntityState.Deleted ? null : (int?)row.ScorePercent;
            // SaveChanges can be retried after a transient DB error; preserve
            // at most one pending outbox row for the same state transition.
            if (ChangeTracker.Entries<AdminSolutionFeedEntry>().Any(x =>
                x.State == EntityState.Added && x.Entity.ItemId == row.Id &&
                x.Entity.Kind == row.Kind && x.Entity.UserId == row.UserId &&
                x.Entity.Status == status && x.Entity.Score == score)) continue;
            AdminSolutionFeedEvents.Add(new AdminSolutionFeedEntry
            {
                Kind = row.Kind, ItemId = row.Id, UserId = row.UserId,
                AssignmentId = row.TaskAssignmentId,
                Status = status, Score = score
            });
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        CaptureAdminSolutionEvents();
        CaptureInteractiveAttemptEvents();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        CaptureAdminSolutionEvents();
        CaptureInteractiveAttemptEvents();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AdminSolutionFeedEntry.Configure(modelBuilder);
        modelBuilder.Entity<ServiceSchemaMarker>(entity =>
        {
            entity.ToTable("ServiceSchemaMarkers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<UserRating>(entity =>
        {
            entity.ToTable("UserRatings");
            entity.HasKey(x => x.UserId);
            entity.HasIndex(x => new { x.TotalScore, x.SolvedCount });
        });

        modelBuilder.Entity<RatingAcceptedActivity>(entity =>
        {
            entity.ToTable("RatingAcceptedActivities");
            entity.HasKey(x => new { x.SourceKind, x.SourceId });
            entity.Property(x => x.SourceKind).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => new { x.UserId, x.AssignmentId });
            entity.HasIndex(x => new { x.CourseId, x.SubmittedAt });
            entity.HasIndex(x => x.SubmittedAt);
        });

        modelBuilder.Entity<LeaderboardEntry>(entity =>
        {
            entity.ToTable("LeaderboardEntries");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Scope, x.CourseId, x.GroupId, x.Rank });
            entity.Property(x => x.Scope).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CourseId).HasMaxLength(80);
            entity.Property(x => x.GroupId).HasMaxLength(80);
        });

        modelBuilder.Entity<RatingProjectionCheckpoint>(entity =>
        {
            entity.ToTable("RatingProjectionCheckpoints");
            entity.HasKey(x => x.ProjectionName);
            entity.Property(x => x.ProjectionName).HasMaxLength(160);
        });

        modelBuilder.Entity<RatingDirtyUser>(entity =>
        {
            entity.ToTable("RatingDirtyUsers");
            entity.HasKey(x => x.UserId);
            entity.HasIndex(x => x.MarkedAtUtc);
            entity.Property(x => x.Reason).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<SolutionSubmission>(entity =>
        {
            entity.ToTable("SolutionSubmissions", table =>
            {
                table.HasCheckConstraint("CK_SolutionSubmissions_SqlBinding", "(\"SqlSpecVersionId\" IS NULL AND \"SqlEngineProfileId\" IS NULL) OR (\"SqlSpecVersionId\" IS NOT NULL AND \"SqlEngineProfileId\" IS NOT NULL AND \"ExecutionTarget\" IS NOT NULL AND length(\"ExecutionTarget\") > 0)");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
            entity.HasIndex(x => x.AssignmentId);
            entity.HasIndex(x => new { x.SqlSpecVersionId, x.SqlEngineProfileId });
            entity.Property(x => x.ExecutionTarget).HasMaxLength(160);
            entity.Property(x => x.ExecutionTarget).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(x => x.SqlSpecVersionId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(x => x.SqlEngineProfileId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(x => x.Language).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
        });

        modelBuilder.Entity<TaskAttempt>(entity =>
        {
            entity.ToTable("TaskAttempts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.Kind, x.TaskAssignmentId, x.SubmittedAt });
            entity.HasIndex(x => new { x.TaskAssignmentId, x.Kind, x.AttemptNumber });
            entity.HasIndex(x => new { x.UserId, x.Kind, x.TaskAssignmentId })
                .IsUnique().HasFilter("\"SubmittedAt\" IS NULL");
            entity.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            entity.Property(x => x.OrderJson).HasColumnType("jsonb");
            entity.Property(x => x.AnswersJson).HasColumnType("jsonb");
            entity.Property(x => x.ReviewJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Badge>(entity =>
        {
            entity.ToTable("Badges");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1024);
            entity.Property(x => x.ImageUrl).HasMaxLength(40000).IsRequired();
        });

        modelBuilder.Entity<UserBadge>(entity =>
        {
            entity.ToTable("UserBadges");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.BadgeId }).IsUnique();
        });

        modelBuilder.Entity<UserQuotaBucket>(entity =>
        {
            entity.ToTable("UserQuotaBuckets");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.BucketType }).IsUnique();
            entity.Property(x => x.BucketType).HasMaxLength(40).IsRequired();
        });

        modelBuilder.Entity<UserImageTaskSolution>(entity =>
        {
            entity.ToTable("UserImageTaskSolutions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
            entity.HasIndex(x => x.AssignmentId);
            entity.Property(x => x.Language).HasMaxLength(40);
        });
    }
}
