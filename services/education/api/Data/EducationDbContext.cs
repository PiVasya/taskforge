using Microsoft.EntityFrameworkCore;
using TaskForge.Education.Api.Domain;

namespace TaskForge.Education.Api.Data;

public sealed class EducationDbContext(DbContextOptions<EducationDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseMap> CourseMaps => Set<CourseMap>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceSchemaMarker>(entity =>
        {
            entity.ToTable("ServiceSchemaMarkers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<Course>(entity =>
        {
            entity.ToTable("Courses");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(4000);
            entity.HasIndex(x => new { x.ParentCourseId, x.Sort });
        });

        modelBuilder.Entity<CourseMap>(entity =>
        {
            entity.ToTable("CourseMaps");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.RootCourseId).IsUnique();
            entity.HasOne<Course>()
                .WithOne()
                .HasForeignKey<CourseMap>(x => x.RootCourseId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.DocumentJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Version).IsRequired();
        });

        modelBuilder.Entity<Group>(entity =>
        {
            entity.ToTable("Groups");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(240).IsRequired();
            entity.Property(x => x.Code).HasMaxLength(80);
        });

        modelBuilder.Entity<GroupMember>(entity =>
        {
            entity.ToTable("GroupMembers");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.GroupId, x.UserId }).IsUnique();
        });
    }
}
