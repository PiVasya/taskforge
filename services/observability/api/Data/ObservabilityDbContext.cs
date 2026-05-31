using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Domain;

namespace TaskForge.Observability.Api.Data;

public sealed class ObservabilityDbContext(DbContextOptions<ObservabilityDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<PageView> PageViews => Set<PageView>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceSchemaMarker>(entity =>
        {
            entity.ToTable("ServiceSchemaMarkers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<PageView>(entity =>
        {
            entity.ToTable("PageViews");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CreatedAt);
            entity.Property(x => x.Path).HasMaxLength(1000).IsRequired();
        });
    }
}
