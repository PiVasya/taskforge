using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Domain;

namespace TaskForge.Solutions.Api.Data;

public sealed class SolutionsDbContext(DbContextOptions<SolutionsDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<UserRating> UserRatings => Set<UserRating>();
    public DbSet<LeaderboardEntry> LeaderboardEntries => Set<LeaderboardEntry>();
    public DbSet<RatingDirtyUser> RatingDirtyUsers => Set<RatingDirtyUser>();
    public DbSet<RatingProjectionCheckpoint> RatingProjectionCheckpoints => Set<RatingProjectionCheckpoint>();
    public DbSet<SolutionSubmission> Submissions => Set<SolutionSubmission>();
    public DbSet<Badge> Badges => Set<Badge>();
    public DbSet<UserBadge> UserBadges => Set<UserBadge>();
    public DbSet<UserQuotaBucket> UserQuotaBuckets => Set<UserQuotaBucket>();
    public DbSet<UserImageTaskSolution> ImageSolutions => Set<UserImageTaskSolution>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
            entity.ToTable("SolutionSubmissions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
            entity.HasIndex(x => x.AssignmentId);
            entity.Property(x => x.Language).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
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
