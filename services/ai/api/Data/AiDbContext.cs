using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Domain;

namespace TaskForge.Ai.Api.Data;

public sealed class AiDbContext(DbContextOptions<AiDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<AiConversation> Conversations => Set<AiConversation>();
    public DbSet<AiMessage> Messages => Set<AiMessage>();
    public DbSet<AiRun> Runs => Set<AiRun>();
    public DbSet<AiStep> Steps => Set<AiStep>();
    public DbSet<AiArtifact> Artifacts => Set<AiArtifact>();
    public DbSet<AccountAnalysisRun> AccountAnalysisRuns => Set<AccountAnalysisRun>();
    public DbSet<AccountAnalysisFinding> AccountAnalysisFindings => Set<AccountAnalysisFinding>();
    public DbSet<AccountAnalysisReview> AccountAnalysisReviews => Set<AccountAnalysisReview>();

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
            entity.HasIndex(x => x.CourseId);
            entity.HasIndex(x => x.AssignmentId);
        });

        modelBuilder.Entity<AiMessage>(entity =>
        {
            entity.ToTable("AiMessages");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ConversationId, x.CreatedAtUtc });
            entity.HasIndex(x => x.RunId);
            entity.Property(x => x.Role).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ClientMessageId).HasMaxLength(120);
        });

        modelBuilder.Entity<AiRun>(entity =>
        {
            entity.ToTable("AiRuns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(80).IsRequired();
            entity.Property(x => x.JobType).HasMaxLength(120).IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.Property(x => x.ErrorJson).HasColumnType("jsonb");
            entity.Property(x => x.WorkerId).HasMaxLength(120);
            entity.HasIndex(x => new { x.Status, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.ConversationId, x.CreatedAtUtc });
        });



        modelBuilder.Entity<AiStep>(entity =>
        {
            entity.ToTable("AiSteps");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.RunId, x.Seq });
            entity.HasIndex(x => new { x.ConversationId, x.CreatedAtUtc });
            entity.Property(x => x.Kind).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(80).IsRequired();
            entity.Property(x => x.ActionName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.DataJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<AiArtifact>(entity =>
        {
            entity.ToTable("AiArtifacts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.RunId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.ConversationId, x.CreatedAtUtc });
            entity.Property(x => x.Type).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.DataJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<AccountAnalysisRun>(entity =>
        {
            entity.ToTable("AccountAnalysisRuns");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Status, x.CreatedAtUtc });
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Phase).HasMaxLength(80).IsRequired();
            entity.Property(x => x.AlgorithmVersion).HasMaxLength(120).IsRequired();
            entity.Property(x => x.SourcesJson).HasColumnType("jsonb");
            entity.Property(x => x.ErrorJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<AccountAnalysisFinding>(entity =>
        {
            entity.ToTable("AccountAnalysisFindings");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.RunId, x.Kind, x.Score });
            entity.HasIndex(x => new { x.RunId, x.FindingKey }).IsUnique();
            entity.HasIndex(x => new { x.PrimaryUserId, x.SecondaryUserId });
            entity.Property(x => x.FindingKey).HasMaxLength(180).IsRequired();
            entity.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.DataJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<AccountAnalysisReview>(entity =>
        {
            entity.ToTable("AccountAnalysisReviews");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.SubjectType, x.SubjectKey }).IsUnique();
            entity.HasIndex(x => x.UserId);
            entity.Property(x => x.SubjectType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.SubjectKey).HasMaxLength(180).IsRequired();
            entity.Property(x => x.Decision).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Note).HasMaxLength(1000);
            entity.Property(x => x.SignalsJson).HasColumnType("jsonb");
            entity.Property(x => x.DataJson).HasColumnType("jsonb");
        });
    }
}
