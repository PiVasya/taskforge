using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Domain;

namespace TaskForge.Ai.Api.Data;

public sealed class AiDbContext(DbContextOptions<AiDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<AiConversation> Conversations => Set<AiConversation>();
    public DbSet<AiMessage> Messages => Set<AiMessage>();
    public DbSet<AiRun> Runs => Set<AiRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceSchemaMarker>(entity =>
        {
            entity.ToTable("ServiceSchemaMarkers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<AiConversation>(entity =>
        {
            entity.ToTable("AiConversations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<AiMessage>(entity =>
        {
            entity.ToTable("AiMessages");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ConversationId, x.CreatedAtUtc });
            entity.Property(x => x.Role).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ClientMessageId).HasMaxLength(120);
        });

        modelBuilder.Entity<AiRun>(entity =>
        {
            entity.ToTable("AiRuns");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ConversationId, x.CreatedAtUtc });
            entity.Property(x => x.Status).HasMaxLength(80).IsRequired();
        });
    }
}
