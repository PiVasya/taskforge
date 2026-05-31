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
            entity.ToTable("ExecutionJobs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Status, x.CreatedAt });
            entity.Property(x => x.Language).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
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
