using Microsoft.EntityFrameworkCore;
using QuizTaskService.Data.Entities;

namespace QuizTaskService.Data;

public sealed class QuizDbContext : DbContext
{
    public QuizDbContext(DbContextOptions<QuizDbContext> options) : base(options)
    {
    }

    public DbSet<QuizTask> Tasks => Set<QuizTask>();
    public DbSet<QuizTaskVersion> TaskVersions => Set<QuizTaskVersion>();
    public DbSet<QuizAttempt> Attempts => Set<QuizAttempt>();
    public DbSet<QuizProgress> Progress => Set<QuizProgress>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QuizTask>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(180).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.SubjectCode).HasMaxLength(80).IsRequired();
            entity.Property(x => x.ExamCode).HasMaxLength(80).IsRequired();
            entity.Property(x => x.SectionCode).HasMaxLength(80);
            entity.Property(x => x.SourceName).HasMaxLength(120);
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasIndex(x => new { x.SubjectCode, x.ExamCode, x.SectionCode });
        });

        modelBuilder.Entity<QuizTaskVersion>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TaskId, x.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<QuizAttempt>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.ClientAttemptId }).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.TaskId, x.CreatedAt });
        });

        modelBuilder.Entity<QuizProgress>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.TaskId }).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.Solved });
        });
    }
}
