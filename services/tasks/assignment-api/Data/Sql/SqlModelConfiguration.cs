using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Domain.Sql;

namespace TaskForge.Tasks.Api.Data.Sql;

internal static class SqlModelConfiguration
{
    internal static void ConfigureSqlDomain(this ModelBuilder model)
    {
        model.Entity<SqlDataset>(e =>
        {
            e.ToTable("SqlDatasets", t =>
            {
                t.HasCheckConstraint("CK_SqlDatasets_Scope", "\"AccessScope\" IN ('private', 'editor-library')");
                t.HasCheckConstraint("CK_SqlDatasets_Owner", "(\"AccessScope\" <> 'private' OR \"OwnerUserId\" IS NOT NULL) AND (\"OwnerUserId\" IS NULL OR \"OwnerUserId\" <> '00000000-0000-0000-0000-000000000000'::uuid)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(8000);
            e.Property(x => x.AccessScope).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.OwnerUserId);
            e.HasIndex(x => new { x.AccessScope, x.IsArchived });
            Mutable(e);
        });

        model.Entity<SqlDatasetVersion>(e =>
        {
            e.ToTable("SqlDatasetVersions", t =>
            {
                t.HasCheckConstraint("CK_SqlDatasetVersions_Version", "\"Version\" > 0 AND \"DefinitionSchemaVersion\" > 0");
                t.HasCheckConstraint("CK_SqlDatasetVersions_Hash", "\"ContentHash\" ~ '^[0-9a-f]{64}$'");
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DatasetId, x.Version }).IsUnique();
            e.HasIndex(x => x.ContentHash);
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            Json(e.Property(x => x.DefinitionJson));
            Json(e.Property(x => x.SeedJson));
            Json(e.Property(x => x.EngineOverridesJson));
            e.HasOne<SqlDataset>().WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Restrict);
            Immutable(e);
        });

        model.Entity<SqlEngineProfile>(e =>
        {
            e.ToTable("SqlEngineProfiles", t =>
            {
                t.HasCheckConstraint("CK_SqlEngineProfiles_Version", "\"Revision\" > 0 AND \"SettingsSchemaVersion\" > 0");
                t.HasCheckConstraint("CK_SqlEngineProfiles_Fingerprint", "\"Fingerprint\" ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("CK_SqlEngineProfiles_RuntimeDigest", "\"RuntimeDigest\" ~ '^sha256:[0-9a-f]{64}$'");
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Key, x.Revision }).IsUnique();
            e.HasIndex(x => x.Fingerprint).IsUnique();
            e.Property(x => x.Key).HasMaxLength(80).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
            e.Property(x => x.Engine).HasMaxLength(32).IsRequired();
            e.Property(x => x.EngineVersion).HasMaxLength(80).IsRequired();
            e.Property(x => x.RuntimeDigest).HasMaxLength(71).IsRequired();
            e.Property(x => x.AdapterVersion).HasMaxLength(80).IsRequired();
            e.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            Json(e.Property(x => x.SettingsJson));
            Immutable(e);
        });

        model.Entity<SqlAssignmentSpec>(e =>
        {
            e.ToTable("SqlAssignmentSpecs");
            e.HasKey(x => x.AssignmentId);
            e.Property(x => x.AssignmentId).ValueGeneratedNever();
            e.HasOne<Assignment>().WithOne().HasForeignKey<SqlAssignmentSpec>(x => x.AssignmentId).OnDelete(DeleteBehavior.Restrict);
            // Composite FKs prevent pointing to another assignment's revision.
            e.HasOne<SqlAssignmentSpecVersion>().WithMany()
                .HasForeignKey(x => new { x.AssignmentId, x.DraftVersionId })
                .HasPrincipalKey(x => new { x.AssignmentId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SqlAssignmentSpecVersion>().WithMany()
                .HasForeignKey(x => new { x.AssignmentId, x.PublishedVersionId })
                .HasPrincipalKey(x => new { x.AssignmentId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            Mutable(e);
        });

        model.Entity<SqlAssignmentSpecVersion>(e =>
        {
            e.ToTable("SqlAssignmentSpecVersions", t =>
            {
                t.HasCheckConstraint("CK_SqlSpecVersions_Version", "\"Version\" > 0 AND \"ContractVersion\" > 0");
                t.HasCheckConstraint("CK_SqlSpecVersions_Mode", "\"Mode\" IN ('result', 'state', 'schema')");
                t.HasCheckConstraint("CK_SqlSpecVersions_Hash", "\"SpecHash\" ~ '^[0-9a-f]{64}$'");
            });
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.AssignmentId, x.Id });
            e.HasIndex(x => new { x.AssignmentId, x.Version }).IsUnique();
            e.HasIndex(x => x.SpecHash);
            e.Property(x => x.Mode).HasMaxLength(16).IsRequired();
            e.Property(x => x.StarterSql).HasMaxLength(100000);
            e.Property(x => x.ReferenceSql).HasMaxLength(100000);
            e.Property(x => x.VerifierVersion).HasMaxLength(80).IsRequired();
            e.Property(x => x.SpecHash).HasMaxLength(64).IsRequired();
            Json(e.Property(x => x.ResultComparisonSettingsJson));
            Json(e.Property(x => x.StateCheckSettingsJson));
            Json(e.Property(x => x.SchemaCheckSettingsJson));
            Json(e.Property(x => x.LimitsJson));
            e.HasOne<SqlAssignmentSpec>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SqlDatasetVersion>().WithMany().HasForeignKey(x => x.DatasetVersionId).OnDelete(DeleteBehavior.Restrict);
            Immutable(e);
        });

        model.Entity<SqlAssignmentEngineTarget>(e =>
        {
            e.ToTable("SqlAssignmentEngineTargets", t => t.HasCheckConstraint("CK_SqlEngineTargets_Sort", "\"Sort\" >= 0"));
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SpecVersionId, x.EngineProfileId }).IsUnique();
            e.Property(x => x.StarterSqlOverride).HasMaxLength(100000);
            e.Property(x => x.ReferenceSqlOverride).HasMaxLength(100000);
            e.Property(x => x.ResultComparisonSettingsOverrideJson).HasColumnType("jsonb");
            e.Property(x => x.StateCheckSettingsOverrideJson).HasColumnType("jsonb");
            e.Property(x => x.SchemaCheckSettingsOverrideJson).HasColumnType("jsonb");
            e.HasOne<SqlAssignmentSpecVersion>().WithMany().HasForeignKey(x => x.SpecVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SqlEngineProfile>().WithMany().HasForeignKey(x => x.EngineProfileId).OnDelete(DeleteBehavior.Restrict);
            Immutable(e);
        });

        model.Entity<SqlDatasetEngineValidation>(e =>
        {
            e.ToTable("SqlDatasetEngineValidations", t =>
            {
                t.HasCheckConstraint("CK_SqlDatasetValidations_Status", "\"Status\" IN ('pending', 'validating', 'valid', 'invalid')");
                t.HasCheckConstraint("CK_SqlDatasetValidations_Key", "\"MaterializationKey\" ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("CK_SqlDatasetValidations_Valid", "\"Status\" <> 'valid' OR (\"ValidatedAt\" IS NOT NULL AND \"ErrorCode\" IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DatasetVersionId, x.EngineProfileId }).IsUnique();
            // Content-equivalent dataset versions share a worker cache, not ownership receipts.
            e.HasIndex(x => x.MaterializationKey);
            e.HasIndex(x => new { x.Status, x.UpdatedAt });
            e.HasIndex(x => x.ExecutionJobId);
            e.Property(x => x.MaterializationKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.ErrorCode).HasMaxLength(100);
            e.Property(x => x.DiagnosticJson).HasColumnType("jsonb");
            e.HasOne<SqlDatasetVersion>().WithMany().HasForeignKey(x => x.DatasetVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SqlEngineProfile>().WithMany().HasForeignKey(x => x.EngineProfileId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.DatasetVersionId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            e.Property(x => x.EngineProfileId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            e.Property(x => x.MaterializationKey).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            Mutable(e);
        });

        model.Entity<SqlExpectedArtifact>(e =>
        {
            e.ToTable("SqlExpectedArtifacts", t =>
            {
                t.HasCheckConstraint("CK_SqlExpectedArtifacts_Status", "\"Status\" IN ('pending', 'validating', 'valid', 'invalid')");
                t.HasCheckConstraint("CK_SqlExpectedArtifacts_Key", "\"ArtifactKey\" ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("CK_SqlExpectedArtifacts_Hash", "\"ContentHash\" IS NULL OR \"ContentHash\" ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("CK_SqlExpectedArtifacts_Size", "\"FormatVersion\" > 0 AND (\"ByteLength\" IS NULL OR \"ByteLength\" >= 0)");
                t.HasCheckConstraint("CK_SqlExpectedArtifacts_Storage", "NOT (\"ExpectedJson\" IS NOT NULL AND \"ObjectKey\" IS NOT NULL)");
                t.HasCheckConstraint("CK_SqlExpectedArtifacts_Valid", "\"Status\" <> 'valid' OR (\"ValidatedAt\" IS NOT NULL AND \"ErrorCode\" IS NULL AND \"ContentHash\" IS NOT NULL AND \"ByteLength\" IS NOT NULL AND (\"ExpectedJson\" IS NOT NULL OR \"ObjectKey\" IS NOT NULL))");
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.EngineTargetId).IsUnique();
            e.HasIndex(x => x.ArtifactKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.UpdatedAt });
            e.HasIndex(x => x.ExecutionJobId);
            e.Property(x => x.ArtifactKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.ExpectedJson).HasColumnType("jsonb");
            e.Property(x => x.ObjectKey).HasMaxLength(1024);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.ErrorCode).HasMaxLength(100);
            e.Property(x => x.DiagnosticJson).HasColumnType("jsonb");
            e.HasOne<SqlAssignmentEngineTarget>().WithOne().HasForeignKey<SqlExpectedArtifact>(x => x.EngineTargetId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.EngineTargetId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            e.Property(x => x.ArtifactKey).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            e.Property(x => x.FormatVersion).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            Mutable(e);
        });
    }

    private static void Json(PropertyBuilder<string> property) => property.HasColumnType("jsonb").IsRequired();

    private static void Mutable<T>(EntityTypeBuilder<T> entity) where T : class, ISqlMutableEntity
        => entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();

    private static void Immutable<T>(EntityTypeBuilder<T> entity) where T : class, ISqlImmutableEntity
    {
        foreach (var property in entity.Metadata.GetProperties())
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
