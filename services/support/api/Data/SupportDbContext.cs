using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Domain;

namespace TaskForge.Support.Api.Data;

public sealed class SupportDbContext(DbContextOptions<SupportDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<SupportTicket> Tickets => Set<SupportTicket>();
    public DbSet<SupportMessage> Messages => Set<SupportMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceSchemaMarker>(entity =>
        {
            entity.ToTable("ServiceSchemaMarkers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<SupportTicket>(entity =>
        {
            entity.ToTable("SupportTickets");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
            entity.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
        });

        modelBuilder.Entity<SupportMessage>(entity =>
        {
            entity.ToTable("SupportMessages");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TicketId, x.CreatedAt });
            entity.Property(x => x.AuthorRole).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Text).HasMaxLength(8000);
            entity.Property(x => x.Source).HasMaxLength(80);
            entity.HasIndex(x => x.TelegramMessageId);
            entity.HasIndex(x => new { x.TelegramChatId, x.TelegramMessageId });
        });
    }
}
