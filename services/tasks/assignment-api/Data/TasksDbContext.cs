using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Domain;

namespace TaskForge.Tasks.Api.Data;

public sealed class TasksDbContext(DbContextOptions<TasksDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<TaskAttempt> Attempts => Set<TaskAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceSchemaMarker>(entity =>
        {
            entity.ToTable("ServiceSchemaMarkers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<Assignment>(entity =>
        {
            entity.ToTable("Assignments");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CourseId, x.Sort });
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(8000);
            entity.Property(x => x.Type).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(40).IsRequired();
        });

        modelBuilder.Entity<TaskAttempt>(entity =>
        {
            entity.ToTable("TaskAttempts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.Kind, x.TaskAssignmentId, x.SubmittedAt });
            entity.HasIndex(x => new { x.TaskAssignmentId, x.Kind, x.AttemptNumber });
            entity.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            entity.Property(x => x.OrderJson).HasColumnType("jsonb");
            entity.Property(x => x.AnswersJson).HasColumnType("jsonb");
            entity.Property(x => x.ReviewJson).HasColumnType("jsonb");
        });
    }
}
