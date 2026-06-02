using Microsoft.EntityFrameworkCore;
using TaskForge.Identity.Api.Domain;

namespace TaskForge.Identity.Api.Data;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<IdentityUser> Users => Set<IdentityUser>();
    public DbSet<UserUiSettings> UiSettings => Set<UserUiSettings>();
    public DbSet<UserLoginLog> LoginLogs => Set<UserLoginLog>();
    public DbSet<FeatureRole> FeatureRoles => Set<FeatureRole>();
    public DbSet<UserFeatureRole> UserFeatureRoles => Set<UserFeatureRole>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceSchemaMarker>(entity =>
        {
            entity.ToTable("ServiceSchemaMarkers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<IdentityUser>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.Email).HasMaxLength(320).IsRequired();
            entity.Property(x => x.FirstName).HasMaxLength(120);
            entity.Property(x => x.LastName).HasMaxLength(120);
            entity.Property(x => x.PasswordSalt).HasMaxLength(256).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Role).HasMaxLength(80).IsRequired();
        });

        modelBuilder.Entity<UserUiSettings>(entity =>
        {
            entity.ToTable("UserUiSettings");
            entity.HasKey(x => x.UserId);
        });

        modelBuilder.Entity<UserLoginLog>(entity =>
        {
            entity.ToTable("UserLoginLogs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.LoginAt });
            entity.Property(x => x.IpAddress).HasMaxLength(80);
            entity.Property(x => x.UserAgent).HasMaxLength(800);
        });

        modelBuilder.Entity<FeatureRole>(entity =>
        {
            entity.ToTable("FeatureRoles");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.Code).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(180).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<UserFeatureRole>(entity =>
        {
            entity.ToTable("UserFeatureRoles");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.Code }).IsUnique();
            entity.Property(x => x.Code).HasMaxLength(80).IsRequired();
        });
    }
}
