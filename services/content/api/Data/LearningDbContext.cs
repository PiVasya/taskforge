using LearningContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LearningContentService.Data;

public sealed class LearningDbContext : DbContext
{
    public LearningDbContext(DbContextOptions<LearningDbContext> options) : base(options)
    {
    }

    public DbSet<LearningCourse> Courses => Set<LearningCourse>();
    public DbSet<LearningPage> Pages => Set<LearningPage>();
    public DbSet<LearningConspect> Conspects => Set<LearningConspect>();
    public DbSet<LearningCourseTaskLink> CourseTaskLinks => Set<LearningCourseTaskLink>();
    public DbSet<LearningConspectTaskLink> ConspectTaskLinks => Set<LearningConspectTaskLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LearningCourse>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.ShortTitle).HasMaxLength(80);
            entity.Property(x => x.SubjectCode).HasMaxLength(80);
            entity.Property(x => x.ExamCode).HasMaxLength(80);
            entity.Property(x => x.SectionCode).HasMaxLength(80);
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasIndex(x => x.ParentCourseId);
            entity.HasIndex(x => new { x.SubjectCode, x.ExamCode, x.SectionCode });
        });

        modelBuilder.Entity<LearningPage>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Kind).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => new { x.CourseId, x.Slug }).IsUnique();
            entity.HasIndex(x => new { x.CourseId, x.SortOrder });
        });

        modelBuilder.Entity<LearningCourseTaskLink>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TaskType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.SourceService).HasMaxLength(120).IsRequired();
            entity.Property(x => x.TaskSlug).HasMaxLength(160);
            entity.Property(x => x.Title).HasMaxLength(300);
            entity.Property(x => x.GroupTitle).HasMaxLength(160);
            entity.HasIndex(x => new { x.CourseId, x.SortOrder });
            entity.HasIndex(x => x.TaskId);
        });

        modelBuilder.Entity<LearningConspect>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(180).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Subtitle).HasMaxLength(300);
            entity.Property(x => x.Kind).HasMaxLength(80).IsRequired();
            entity.Property(x => x.SubjectCode).HasMaxLength(80);
            entity.Property(x => x.ExamCode).HasMaxLength(80);
            entity.Property(x => x.SectionCode).HasMaxLength(80);
            entity.HasIndex(x => new { x.CourseId, x.Slug }).IsUnique();
            entity.HasIndex(x => new { x.SubjectCode, x.ExamCode, x.SectionCode });
            entity.HasIndex(x => new { x.CourseId, x.SortOrder });
        });

        modelBuilder.Entity<LearningConspectTaskLink>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TaskType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.SourceService).HasMaxLength(120).IsRequired();
            entity.Property(x => x.TaskSlug).HasMaxLength(180);
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.ButtonText).HasMaxLength(80).IsRequired();
            entity.Property(x => x.GroupTitle).HasMaxLength(160);
            entity.Property(x => x.AnchorBlockId).HasMaxLength(120);
            entity.HasIndex(x => new { x.ConspectId, x.SortOrder });
            entity.HasIndex(x => x.TaskId);
            entity.HasIndex(x => x.TaskSlug);
        });
    }
}
