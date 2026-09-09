using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Tasks.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSqlDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SqlDatasets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccessScope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlDatasets", x => x.Id);
                    table.CheckConstraint("CK_SqlDatasets_Owner", "(\"AccessScope\" <> 'private' OR \"OwnerUserId\" IS NOT NULL) AND (\"OwnerUserId\" IS NULL OR \"OwnerUserId\" <> '00000000-0000-0000-0000-000000000000'::uuid)");
                    table.CheckConstraint("CK_SqlDatasets_Scope", "\"AccessScope\" IN ('private', 'editor-library')");
                });

            migrationBuilder.CreateTable(
                name: "SqlEngineProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Engine = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EngineVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RuntimeDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    AdapterVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SettingsSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    SettingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlEngineProfiles", x => x.Id);
                    table.CheckConstraint("CK_SqlEngineProfiles_Fingerprint", "\"Fingerprint\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_SqlEngineProfiles_RuntimeDigest", "\"RuntimeDigest\" ~ '^sha256:[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_SqlEngineProfiles_Version", "\"Revision\" > 0 AND \"SettingsSchemaVersion\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "SqlDatasetVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DatasetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DefinitionSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    SeedJson = table.Column<string>(type: "jsonb", nullable: false),
                    EngineOverridesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlDatasetVersions", x => x.Id);
                    table.CheckConstraint("CK_SqlDatasetVersions_Hash", "\"ContentHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_SqlDatasetVersions_Version", "\"Version\" > 0 AND \"DefinitionSchemaVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_SqlDatasetVersions_SqlDatasets_DatasetId",
                        column: x => x.DatasetId,
                        principalTable: "SqlDatasets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SqlDatasetEngineValidations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DatasetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EngineProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaterializationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ValidationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DiagnosticJson = table.Column<string>(type: "jsonb", nullable: true),
                    ValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlDatasetEngineValidations", x => x.Id);
                    table.CheckConstraint("CK_SqlDatasetValidations_Key", "\"MaterializationKey\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_SqlDatasetValidations_Status", "\"Status\" IN ('pending', 'validating', 'valid', 'invalid')");
                    table.CheckConstraint("CK_SqlDatasetValidations_Valid", "\"Status\" <> 'valid' OR (\"ValidatedAt\" IS NOT NULL AND \"ErrorCode\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_SqlDatasetEngineValidations_SqlDatasetVersions_DatasetVersi~",
                        column: x => x.DatasetVersionId,
                        principalTable: "SqlDatasetVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SqlDatasetEngineValidations_SqlEngineProfiles_EngineProfile~",
                        column: x => x.EngineProfileId,
                        principalTable: "SqlEngineProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SqlAssignmentEngineTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpecVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EngineProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Sort = table.Column<int>(type: "integer", nullable: false),
                    StarterSqlOverride = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    ReferenceSqlOverride = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    ResultComparisonSettingsOverrideJson = table.Column<string>(type: "jsonb", nullable: true),
                    StateCheckSettingsOverrideJson = table.Column<string>(type: "jsonb", nullable: true),
                    SchemaCheckSettingsOverrideJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlAssignmentEngineTargets", x => x.Id);
                    table.CheckConstraint("CK_SqlEngineTargets_Sort", "\"Sort\" >= 0");
                    table.ForeignKey(
                        name: "FK_SqlAssignmentEngineTargets_SqlEngineProfiles_EngineProfileId",
                        column: x => x.EngineProfileId,
                        principalTable: "SqlEngineProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SqlExpectedArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EngineTargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FormatVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ValidationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpectedJson = table.Column<string>(type: "jsonb", nullable: true),
                    ObjectKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ByteLength = table.Column<long>(type: "bigint", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DiagnosticJson = table.Column<string>(type: "jsonb", nullable: true),
                    ValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlExpectedArtifacts", x => x.Id);
                    table.CheckConstraint("CK_SqlExpectedArtifacts_Hash", "\"ContentHash\" IS NULL OR \"ContentHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_SqlExpectedArtifacts_Key", "\"ArtifactKey\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_SqlExpectedArtifacts_Size", "\"FormatVersion\" > 0 AND (\"ByteLength\" IS NULL OR \"ByteLength\" >= 0)");
                    table.CheckConstraint("CK_SqlExpectedArtifacts_Status", "\"Status\" IN ('pending', 'validating', 'valid', 'invalid')");
                    table.CheckConstraint("CK_SqlExpectedArtifacts_Storage", "NOT (\"ExpectedJson\" IS NOT NULL AND \"ObjectKey\" IS NOT NULL)");
                    table.CheckConstraint("CK_SqlExpectedArtifacts_Valid", "\"Status\" <> 'valid' OR (\"ValidatedAt\" IS NOT NULL AND \"ErrorCode\" IS NULL AND \"ContentHash\" IS NOT NULL AND \"ByteLength\" IS NOT NULL AND (\"ExpectedJson\" IS NOT NULL OR \"ObjectKey\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_SqlExpectedArtifacts_SqlAssignmentEngineTargets_EngineTarge~",
                        column: x => x.EngineTargetId,
                        principalTable: "SqlAssignmentEngineTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SqlAssignmentSpecs",
                columns: table => new
                {
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DraftVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlAssignmentSpecs", x => x.AssignmentId);
                    table.ForeignKey(
                        name: "FK_SqlAssignmentSpecs_Assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "Assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SqlAssignmentSpecVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DatasetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StarterSql = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    ReferenceSql = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    AllowMultipleStatements = table.Column<bool>(type: "boolean", nullable: false),
                    ResultComparisonSettingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    StateCheckSettingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SchemaCheckSettingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    LimitsJson = table.Column<string>(type: "jsonb", nullable: false),
                    VerifierVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SpecHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlAssignmentSpecVersions", x => x.Id);
                    table.UniqueConstraint("AK_SqlAssignmentSpecVersions_AssignmentId_Id", x => new { x.AssignmentId, x.Id });
                    table.CheckConstraint("CK_SqlSpecVersions_Hash", "\"SpecHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_SqlSpecVersions_Mode", "\"Mode\" IN ('result', 'state', 'schema')");
                    table.CheckConstraint("CK_SqlSpecVersions_Version", "\"Version\" > 0 AND \"ContractVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_SqlAssignmentSpecVersions_SqlAssignmentSpecs_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "SqlAssignmentSpecs",
                        principalColumn: "AssignmentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SqlAssignmentSpecVersions_SqlDatasetVersions_DatasetVersion~",
                        column: x => x.DatasetVersionId,
                        principalTable: "SqlDatasetVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SqlAssignmentEngineTargets_EngineProfileId",
                table: "SqlAssignmentEngineTargets",
                column: "EngineProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_SqlAssignmentEngineTargets_SpecVersionId_EngineProfileId",
                table: "SqlAssignmentEngineTargets",
                columns: new[] { "SpecVersionId", "EngineProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqlAssignmentSpecs_AssignmentId_DraftVersionId",
                table: "SqlAssignmentSpecs",
                columns: new[] { "AssignmentId", "DraftVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SqlAssignmentSpecs_AssignmentId_PublishedVersionId",
                table: "SqlAssignmentSpecs",
                columns: new[] { "AssignmentId", "PublishedVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SqlAssignmentSpecVersions_AssignmentId_Version",
                table: "SqlAssignmentSpecVersions",
                columns: new[] { "AssignmentId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqlAssignmentSpecVersions_DatasetVersionId",
                table: "SqlAssignmentSpecVersions",
                column: "DatasetVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_SqlAssignmentSpecVersions_SpecHash",
                table: "SqlAssignmentSpecVersions",
                column: "SpecHash");

            migrationBuilder.CreateIndex(
                name: "IX_SqlDatasetEngineValidations_DatasetVersionId_EngineProfileId",
                table: "SqlDatasetEngineValidations",
                columns: new[] { "DatasetVersionId", "EngineProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqlDatasetEngineValidations_EngineProfileId",
                table: "SqlDatasetEngineValidations",
                column: "EngineProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_SqlDatasetEngineValidations_ExecutionJobId",
                table: "SqlDatasetEngineValidations",
                column: "ExecutionJobId");

            migrationBuilder.CreateIndex(
                name: "IX_SqlDatasetEngineValidations_MaterializationKey",
                table: "SqlDatasetEngineValidations",
                column: "MaterializationKey");

            migrationBuilder.CreateIndex(
                name: "IX_SqlDatasetEngineValidations_Status_UpdatedAt",
                table: "SqlDatasetEngineValidations",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SqlDatasets_AccessScope_IsArchived",
                table: "SqlDatasets",
                columns: new[] { "AccessScope", "IsArchived" });

            migrationBuilder.CreateIndex(
                name: "IX_SqlDatasets_OwnerUserId",
                table: "SqlDatasets",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SqlDatasetVersions_ContentHash",
                table: "SqlDatasetVersions",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_SqlDatasetVersions_DatasetId_Version",
                table: "SqlDatasetVersions",
                columns: new[] { "DatasetId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqlEngineProfiles_Fingerprint",
                table: "SqlEngineProfiles",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqlEngineProfiles_Key_Revision",
                table: "SqlEngineProfiles",
                columns: new[] { "Key", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqlExpectedArtifacts_ArtifactKey",
                table: "SqlExpectedArtifacts",
                column: "ArtifactKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqlExpectedArtifacts_EngineTargetId",
                table: "SqlExpectedArtifacts",
                column: "EngineTargetId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqlExpectedArtifacts_ExecutionJobId",
                table: "SqlExpectedArtifacts",
                column: "ExecutionJobId");

            migrationBuilder.CreateIndex(
                name: "IX_SqlExpectedArtifacts_Status_UpdatedAt",
                table: "SqlExpectedArtifacts",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_SqlAssignmentEngineTargets_SqlAssignmentSpecVersions_SpecVe~",
                table: "SqlAssignmentEngineTargets",
                column: "SpecVersionId",
                principalTable: "SqlAssignmentSpecVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SqlAssignmentSpecs_SqlAssignmentSpecVersions_AssignmentId_D~",
                table: "SqlAssignmentSpecs",
                columns: new[] { "AssignmentId", "DraftVersionId" },
                principalTable: "SqlAssignmentSpecVersions",
                principalColumns: new[] { "AssignmentId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SqlAssignmentSpecs_SqlAssignmentSpecVersions_AssignmentId_P~",
                table: "SqlAssignmentSpecs",
                columns: new[] { "AssignmentId", "PublishedVersionId" },
                principalTable: "SqlAssignmentSpecVersions",
                principalColumns: new[] { "AssignmentId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SqlAssignmentSpecs_SqlAssignmentSpecVersions_AssignmentId_D~",
                table: "SqlAssignmentSpecs");

            migrationBuilder.DropForeignKey(
                name: "FK_SqlAssignmentSpecs_SqlAssignmentSpecVersions_AssignmentId_P~",
                table: "SqlAssignmentSpecs");

            migrationBuilder.DropTable(
                name: "SqlDatasetEngineValidations");

            migrationBuilder.DropTable(
                name: "SqlExpectedArtifacts");

            migrationBuilder.DropTable(
                name: "SqlAssignmentEngineTargets");

            migrationBuilder.DropTable(
                name: "SqlEngineProfiles");

            migrationBuilder.DropTable(
                name: "SqlAssignmentSpecVersions");

            migrationBuilder.DropTable(
                name: "SqlAssignmentSpecs");

            migrationBuilder.DropTable(
                name: "SqlDatasetVersions");

            migrationBuilder.DropTable(
                name: "SqlDatasets");
        }
    }
}
