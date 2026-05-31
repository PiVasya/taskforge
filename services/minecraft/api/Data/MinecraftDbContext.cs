using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Domain;

namespace TaskForge.Minecraft.Api.Data;

public sealed class MinecraftDbContext(DbContextOptions<MinecraftDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<MinecraftLink> Links => Set<MinecraftLink>();
    public DbSet<MinecraftChatMessage> ChatMessages => Set<MinecraftChatMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceSchemaMarker>(entity =>
        {
            entity.ToTable("ServiceSchemaMarkers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<MinecraftLink>(entity =>
        {
            entity.ToTable("MinecraftLinks");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.Code).HasMaxLength(40).IsRequired();
            entity.Property(x => x.PlayerName).HasMaxLength(120);
            entity.Property(x => x.PlayerUuid).HasMaxLength(80);
        });

        modelBuilder.Entity<MinecraftChatMessage>(entity =>
        {
            entity.ToTable("MinecraftChatMessages");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CreatedAt);
            entity.Property(x => x.Author).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Text).HasMaxLength(2000).IsRequired();
        });
    }
}
