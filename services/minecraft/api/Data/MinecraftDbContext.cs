using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Domain;

namespace TaskForge.Minecraft.Api.Data;

public sealed class MinecraftDbContext(DbContextOptions<MinecraftDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<MinecraftLink> Links => Set<MinecraftLink>();
    public DbSet<MinecraftLinkCode> LinkCodes => Set<MinecraftLinkCode>();
    public DbSet<MinecraftChatMessage> ChatMessages => Set<MinecraftChatMessage>();
    public DbSet<MinecraftEconomySettings> EconomySettings => Set<MinecraftEconomySettings>();
    public DbSet<MinecraftWeeklyJoin> WeeklyJoins => Set<MinecraftWeeklyJoin>();
    public DbSet<MinecraftRatingTransaction> RatingTransactions => Set<MinecraftRatingTransaction>();

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
            entity.HasIndex(x => new { x.UserId, x.Confirmed, x.UnlinkedAtUtc });
            entity.Property(x => x.Code).HasMaxLength(80).IsRequired();
            entity.Property(x => x.PlayerName).HasMaxLength(120);
            entity.Property(x => x.PlayerUuid).HasMaxLength(80);
        });

        modelBuilder.Entity<MinecraftLinkCode>(entity =>
        {
            entity.ToTable("MinecraftLinkCodes");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.ExpiresAtUtc, x.UsedAtUtc });
            entity.Property(x => x.Nick).HasMaxLength(32).IsRequired();
            entity.Property(x => x.CodeHash).IsRequired();
            entity.Property(x => x.Salt).IsRequired();
        });

        modelBuilder.Entity<MinecraftChatMessage>(entity =>
        {
            entity.ToTable("MinecraftChatMessages");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.Property(x => x.Source).HasMaxLength(64).IsRequired();
            entity.Property(x => x.AuthorName).HasMaxLength(120);
            entity.Property(x => x.MinecraftNick).HasMaxLength(32);
            entity.Property(x => x.MinecraftUuid).HasMaxLength(80);
            entity.Property(x => x.Message).HasMaxLength(2000).IsRequired();
        });

        modelBuilder.Entity<MinecraftEconomySettings>(entity =>
        {
            entity.ToTable("MinecraftEconomySettings");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.UpdatedAtUtc);
        });

        modelBuilder.Entity<MinecraftWeeklyJoin>(entity =>
        {
            entity.ToTable("MinecraftWeeklyJoins");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.WeekStartUtc }).IsUnique();
        });

        modelBuilder.Entity<MinecraftRatingTransaction>(entity =>
        {
            entity.ToTable("MinecraftRatingTransactions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            entity.HasIndex(x => x.RequestId).IsUnique();
            entity.Property(x => x.PlayerName).HasMaxLength(120);
            entity.Property(x => x.PlayerUuid).HasMaxLength(80);
            entity.Property(x => x.Kind).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            entity.Property(x => x.RequestId).HasMaxLength(120);
            entity.Property(x => x.MetadataJson).HasColumnType("jsonb");
        });
    }
}
