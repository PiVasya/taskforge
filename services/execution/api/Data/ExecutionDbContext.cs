using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Domain;

namespace TaskForge.Execution.Api.Data;

public sealed class ExecutionDbContext(DbContextOptions<ExecutionDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<ExecutionJob> ExecutionJobs => Set<ExecutionJob>();
    public DbSet<ExecutionResult> ExecutionResults => Set<ExecutionResult>();
    public DbSet<RunnerHeartbeat> RunnerHeartbeats => Set<RunnerHeartbeat>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceSchemaMarker>(entity =>
        {
            entity.ToTable("ServiceSchemaMarkers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
        });
        modelBuilder.Entity<ExecutionJob>(entity =>
        {
            entity.ToTable("ExecutionJobs", table =>
            {
                table.HasCheckConstraint("CK_ExecutionJobs_Kind", "\"Kind\" IN ('legacy', 'code', 'image', 'sql-check', 'sql-preview', 'sql-materialize')");
                table.HasCheckConstraint("CK_ExecutionJobs_Payload", "(\"Kind\" = 'legacy' AND \"PayloadVersion\" IS NULL AND \"PayloadJson\" IS NULL) OR (\"Kind\" <> 'legacy' AND \"Target\" IS NOT NULL AND length(\"Target\") > 0 AND \"PayloadVersion\" IS NOT NULL AND \"PayloadVersion\" > 0 AND \"PayloadJson\" IS NOT NULL)");
                table.HasCheckConstraint("CK_ExecutionJobs_SqlSubmission", "(\"Kind\" NOT IN ('sql-preview', 'sql-materialize') OR \"SubmissionId\" IS NULL) AND (\"Kind\" <> 'sql-check' OR (\"SubmissionId\" IS NOT NULL AND \"AssignmentId\" IS NOT NULL AND \"UserId\" IS NOT NULL))");
                table.HasCheckConstraint("CK_ExecutionJobs_SqlNoLegacyTests", "\"Kind\" NOT IN ('sql-check', 'sql-preview', 'sql-materialize') OR (\"TestsJson\" IS NULL AND \"CodeForbiddenCallsJson\" IS NULL AND \"CodeRequiredCallsJson\" IS NULL)");
                table.HasCheckConstraint("CK_ExecutionJobs_Lease", "(\"ClaimedByWorkerId\" IS NULL AND \"LeaseToken\" IS NULL AND \"LeaseExpiresAt\" IS NULL) OR (\"ClaimedByWorkerId\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Status, x.CreatedAt });
            entity.HasIndex(x => x.SubmissionId);
            entity.HasIndex(x => new { x.Status, x.Kind, x.Target, x.CreatedAt });
            entity.HasIndex(x => new { x.Status, x.LeaseExpiresAt });
            entity.HasIndex(x => x.DeduplicationKey).IsUnique();
            entity.Property(x => x.Kind).HasMaxLength(40).HasDefaultValue(ExecutionJobKinds.Legacy).IsRequired();
            entity.Property(x => x.Target).HasMaxLength(160);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.Property(x => x.DeduplicationKey).HasMaxLength(160);
            entity.Property(x => x.ClaimedByWorkerId).HasMaxLength(160);
            entity.Property(x => x.Language).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Code).IsRequired();
            entity.Property(x => x.CodeForbiddenCallsJson).HasColumnType("jsonb");
            entity.Property(x => x.CodeRequiredCallsJson).HasColumnType("jsonb");
        });
        modelBuilder.Entity<ExecutionResult>(entity =>
        {
            entity.ToTable("ExecutionResults");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.JobId).IsUnique();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
        });
        modelBuilder.Entity<RunnerHeartbeat>(entity =>
        {
            entity.ToTable("RunnerHeartbeats");
            entity.HasKey(x => x.RunnerId);
            entity.Property(x => x.Language).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Region).HasMaxLength(80);
        });
    }
}
