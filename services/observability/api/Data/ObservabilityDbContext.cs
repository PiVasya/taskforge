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
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
            entity.HasIndex(x => new { x.Path, x.CreatedAt });
            entity.Property(x => x.Path).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.Method).HasMaxLength(20);
            entity.Property(x => x.Action).HasMaxLength(120);
            entity.Property(x => x.UserAgent).HasMaxLength(800);
            entity.Property(x => x.Source).HasMaxLength(80);
            entity.Property(x => x.TraceId).HasMaxLength(160);
            entity.Property(x => x.ErrorCode).HasMaxLength(120);
            entity.Property(x => x.ErrorMessage).HasMaxLength(1000);
            entity.Property(x => x.ClientIpHash).HasMaxLength(128);
            entity.Property(x => x.ClientIpPrefix).HasMaxLength(80);
            entity.Property(x => x.ClientCountry).HasMaxLength(8);
            entity.HasIndex(x => new { x.Action, x.CreatedAt });
            entity.HasIndex(x => new { x.StatusCode, x.CreatedAt });
            entity.HasIndex(x => new { x.ClientIpHash, x.CreatedAt });
        });
    }
}
