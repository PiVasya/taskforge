using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Domain;

namespace TaskForge.Minecraft.Api.Data;

public sealed class MinecraftDbContext(DbContextOptions<MinecraftDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<MinecraftLink> Links => Set<MinecraftLink>();
    public DbSet<MinecraftLinkCode> LinkCodes => Set<MinecraftLinkCode>();
    public DbSet<MinecraftChatMessage> ChatMessages => Set<MinecraftChatMessage>();
    public DbSet<MinecraftRatingTransaction> RatingTransactions => Set<MinecraftRatingTransaction>();
    public DbSet<MinecraftDeathRecovery> DeathRecoveries => Set<MinecraftDeathRecovery>();

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
            entity.HasIndex(x => x.CreatedAtUtc).HasDatabaseName("IX_MinecraftChatMessages_CreatedAt");
            entity.Property(x => x.Source).HasMaxLength(64).HasDefaultValue("SiteUser").IsRequired();
            entity.Property(x => x.AuthorName).HasColumnName("Author").HasMaxLength(120).IsRequired();
            entity.Property(x => x.MinecraftNick).HasMaxLength(32);
            entity.Property(x => x.MinecraftUuid).HasMaxLength(80);
            entity.Property(x => x.Message).HasColumnName("Text").HasMaxLength(2000).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("CreatedAt");
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

        modelBuilder.Entity<MinecraftDeathRecovery>(entity =>
        {
            entity.ToTable("MinecraftDeathRecoveries");
            entity.HasKey(x => x.DeathId);
            entity.HasIndex(x => new { x.PlayerUuid, x.OfferExpiresAtUtc });
            entity.HasIndex(x => new { x.Stage, x.UpdatedAtUtc });
            entity.HasIndex(x => x.PurchaseRequestId).IsUnique();
            entity.Property(x => x.PlayerName).HasMaxLength(32).IsRequired();
            entity.Property(x => x.WorldKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.WorldName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ItemsPayload).HasColumnType("text").IsRequired();
            entity.Property(x => x.Stage).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(32);
            entity.Property(x => x.PurchaseRequestId).HasMaxLength(120);
            entity.Property(x => x.PaymentStatus).HasMaxLength(32).IsRequired();
            entity.Property(x => x.PaymentErrorCode).HasMaxLength(128);
            entity.Property(x => x.PreviousGameMode).HasMaxLength(32);
            entity.Property(x => x.LastError).HasMaxLength(1024);
        });
    }
}
