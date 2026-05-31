using Microsoft.EntityFrameworkCore;
using TaskForge.Notifications.Api.Domain;

namespace TaskForge.Notifications.Api.Data;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<NotificationItem> Notifications => Set<NotificationItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceSchemaMarker>(entity =>
        {
            entity.ToTable("ServiceSchemaMarkers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<NotificationItem>(entity =>
        {
            entity.ToTable("Notifications");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
            entity.Property(x => x.Type).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(240).IsRequired();
            entity.Property(x => x.Message).HasMaxLength(2000);
        });
    }
}
