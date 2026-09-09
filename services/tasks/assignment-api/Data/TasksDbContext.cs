using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Domain.Sql;
using TaskForge.Tasks.Api.Data.Sql;

namespace TaskForge.Tasks.Api.Data;

public sealed class TasksDbContext(DbContextOptions<TasksDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSchemaMarker> SchemaMarkers => Set<ServiceSchemaMarker>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<TaskAttempt> Attempts => Set<TaskAttempt>();
    public DbSet<AssignmentActivityEvent> AssignmentActivityEvents => Set<AssignmentActivityEvent>();
    public DbSet<AssignmentWorkSession> AssignmentWorkSessions => Set<AssignmentWorkSession>();
    public DbSet<AssignmentCodeSnapshot> AssignmentCodeSnapshots => Set<AssignmentCodeSnapshot>();

    public DbSet<SqlDataset> SqlDatasets => Set<SqlDataset>();
    public DbSet<SqlDatasetVersion> SqlDatasetVersions => Set<SqlDatasetVersion>();
    public DbSet<SqlEngineProfile> SqlEngineProfiles => Set<SqlEngineProfile>();
    public DbSet<SqlAssignmentSpec> SqlAssignmentSpecs => Set<SqlAssignmentSpec>();
    public DbSet<SqlAssignmentSpecVersion> SqlAssignmentSpecVersions => Set<SqlAssignmentSpecVersion>();
    public DbSet<SqlAssignmentEngineTarget> SqlAssignmentEngineTargets => Set<SqlAssignmentEngineTarget>();
    public DbSet<SqlDatasetEngineValidation> SqlDatasetEngineValidations => Set<SqlDatasetEngineValidation>();
    public DbSet<SqlExpectedArtifact> SqlExpectedArtifacts => Set<SqlExpectedArtifact>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SqlDomainSaveGuard.Prepare(this);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SqlDomainSaveGuard.Prepare(this);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigureSqlDomain();

        modelBuilder.Entity<ServiceSchemaMarker>(entity =>
        {
            entity.ToTable("ServiceSchemaMarkers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<Assignment>(entity =>
        {
            entity.ToTable("Assignments");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CourseId, x.Sort });
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(8000);
            entity.Property(x => x.Type).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(40).IsRequired();
            entity.Property(x => x.AllowedLanguagesCsv).HasMaxLength(300);
            entity.Property(x => x.Tags).HasMaxLength(1000);
            entity.Property(x => x.CodeForbiddenCallsJson).HasColumnType("jsonb");
            entity.Property(x => x.CodeRequiredCallsJson).HasColumnType("jsonb");
            entity.Property(x => x.AnalyticsSettingsJson).HasColumnType("jsonb");
        });


        modelBuilder.Entity<AssignmentActivityEvent>(entity =>
        {
            entity.ToTable("AssignmentActivityEvents");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AssignmentId, x.CreatedAt });
            entity.HasIndex(x => new { x.AssignmentId, x.UserId, x.SessionId, x.CreatedAt });
            entity.HasIndex(x => new { x.AssignmentId, x.UserId, x.SessionId, x.EventUid }).IsUnique();
            entity.HasIndex(x => new { x.AssignmentId, x.EventType, x.CreatedAt });
            entity.Property(x => x.SessionId).HasMaxLength(80).IsRequired();
            entity.Property(x => x.EventUid).HasMaxLength(120);
            entity.Property(x => x.EventType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.Property(x => x.CodeHash).HasMaxLength(128);
            entity.Property(x => x.TextHash).HasMaxLength(128);
            entity.Property(x => x.TextSample).HasMaxLength(5000);
            entity.Property(x => x.Language).HasMaxLength(40);
            entity.Property(x => x.RiskReason).HasMaxLength(600);
            entity.Property(x => x.IpHash).HasMaxLength(128);
            entity.Property(x => x.UserAgentHash).HasMaxLength(128);
        });


        modelBuilder.Entity<AssignmentWorkSession>(entity =>
        {
            entity.ToTable("AssignmentWorkSessions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AssignmentId, x.UserId, x.SessionId }).IsUnique();
            entity.HasIndex(x => new { x.AssignmentId, x.RiskScore });
            entity.HasIndex(x => new { x.AssignmentId, x.LastActivityAt });
            entity.Property(x => x.SessionId).HasMaxLength(80).IsRequired();
            entity.Property(x => x.FinalCodeHash).HasMaxLength(128);
            entity.Property(x => x.LastLanguage).HasMaxLength(40);
            entity.Property(x => x.RiskLevel).HasMaxLength(20).IsRequired();
            entity.Property(x => x.RiskReasonsJson).HasColumnType("jsonb");
            entity.Property(x => x.LastEventType).HasMaxLength(80);
        });

        modelBuilder.Entity<AssignmentCodeSnapshot>(entity =>
        {
            entity.ToTable("AssignmentCodeSnapshots");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AssignmentId, x.UserId, x.CreatedAt });
            entity.HasIndex(x => new { x.AssignmentId, x.CodeHash });
            entity.HasIndex(x => new { x.AssignmentId, x.SessionId, x.CreatedAt });
            entity.Property(x => x.SessionId).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Source).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(40);
            entity.Property(x => x.CodeHash).HasMaxLength(128);
            entity.Property(x => x.CodeSample).HasMaxLength(5000);
            entity.Property(x => x.FullCode).HasColumnType("text");
        });

        modelBuilder.Entity<TaskAttempt>(entity =>
        {
            entity.ToTable("TaskAttempts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.Kind, x.TaskAssignmentId, x.SubmittedAt });
            entity.HasIndex(x => new { x.TaskAssignmentId, x.Kind, x.AttemptNumber });
            entity.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            entity.Property(x => x.OrderJson).HasColumnType("jsonb");
            entity.Property(x => x.AnswersJson).HasColumnType("jsonb");
            entity.Property(x => x.ReviewJson).HasColumnType("jsonb");
        });
    }
}
