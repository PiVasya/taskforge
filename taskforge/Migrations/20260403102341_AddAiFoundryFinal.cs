using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class AddAiFoundryFinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentJobId",
                table: "AiJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StageCode",
                table: "AiJobs",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StageLabel",
                table: "AiJobs",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StageOrder",
                table: "AiJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "AiGeneratedAssignmentDrafts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BatchItemId",
                table: "AiGeneratedAssignmentDrafts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentJobId",
                table: "AiGeneratedAssignmentDrafts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArtifactType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    StageCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: true),
                    ModelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiArtifacts_AiGeneratedAssignmentDrafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "AiGeneratedAssignmentDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiArtifacts_AiJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "AiJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    AssignmentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Mode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RequestedCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentStage = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CanonicalRequestJson = table.Column<string>(type: "jsonb", nullable: true),
                    CourseProfileJson = table.Column<string>(type: "jsonb", nullable: true),
                    GapAnalysisJson = table.Column<string>(type: "jsonb", nullable: true),
                    AssignmentOntologyJson = table.Column<string>(type: "jsonb", nullable: true),
                    ExemplarSignalsJson = table.Column<string>(type: "jsonb", nullable: true),
                    NegativeMemoryJson = table.Column<string>(type: "jsonb", nullable: true),
                    CoverageJson = table.Column<string>(type: "jsonb", nullable: true),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: true),
                    SummaryJson = table.Column<string>(type: "jsonb", nullable: true),
                    DecisionSummaryJson = table.Column<string>(type: "jsonb", nullable: true),
                    ReviewLedgerJson = table.Column<string>(type: "jsonb", nullable: true),
                    BatchReviewJson = table.Column<string>(type: "jsonb", nullable: true),
                    StudentJourneyJson = table.Column<string>(type: "jsonb", nullable: true),
                    PublicationAuditJson = table.Column<string>(type: "jsonb", nullable: true),
                    PublishPackJson = table.Column<string>(type: "text", nullable: true),
                    QualityLedgerJson = table.Column<string>(type: "text", nullable: true),
                    ExportManifestJson = table.Column<string>(type: "text", nullable: true),
                    PlannerFeedbackJson = table.Column<string>(type: "jsonb", nullable: true),
                    HistoricalPlannerPriorsJson = table.Column<string>(type: "jsonb", nullable: true),
                    PositiveMemoryJson = table.Column<string>(type: "jsonb", nullable: true),
                    BatchMemoryJson = table.Column<string>(type: "jsonb", nullable: true),
                    InstitutionalMemoryJson = table.Column<string>(type: "jsonb", nullable: true),
                    AntiPatternMemoryJson = table.Column<string>(type: "jsonb", nullable: true),
                    ReplanLedgerJson = table.Column<string>(type: "jsonb", nullable: true),
                    DecisionLogDigestJson = table.Column<string>(type: "jsonb", nullable: true),
                    FeedbackLoopStateJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiBatches_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiBatches_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AiReviewFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: true),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: true),
                    StageCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Severity = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    Code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Message = table.Column<string>(type: "text", nullable: false),
                    SuggestedRepair = table.Column<string>(type: "text", nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiReviewFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiReviewFindings_AiArtifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "AiArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiReviewFindings_AiGeneratedAssignmentDrafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "AiGeneratedAssignmentDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AiBatchItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    TargetSkill = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    DifficultyTarget = table.Column<int>(type: "integer", nullable: false),
                    MicroGoal = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BriefJson = table.Column<string>(type: "jsonb", nullable: true),
                    BriefReviewJson = table.Column<string>(type: "jsonb", nullable: true),
                    ContextReviewJson = table.Column<string>(type: "jsonb", nullable: true),
                    ReferencePackJson = table.Column<string>(type: "jsonb", nullable: true),
                    StylePackJson = table.Column<string>(type: "jsonb", nullable: true),
                    PolicyPackJson = table.Column<string>(type: "jsonb", nullable: true),
                    NegativePackJson = table.Column<string>(type: "jsonb", nullable: true),
                    ExemplarPackJson = table.Column<string>(type: "jsonb", nullable: true),
                    ReferenceSignalsJson = table.Column<string>(type: "jsonb", nullable: true),
                    DecisionLogJson = table.Column<string>(type: "jsonb", nullable: true),
                    PlannerSignalsJson = table.Column<string>(type: "jsonb", nullable: true),
                    HistoricalSlotPriorsJson = table.Column<string>(type: "text", nullable: true),
                    AntiPatternFlagsJson = table.Column<string>(type: "jsonb", nullable: true),
                    ReplanHistoryJson = table.Column<string>(type: "jsonb", nullable: true),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScorecardJson = table.Column<string>(type: "jsonb", nullable: true),
                    RepairCount = table.Column<int>(type: "integer", nullable: false),
                    ParentItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiBatchItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiBatchItems_AiBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "AiBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiDecisionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    BatchItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    StageCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DecisionType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiDecisionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiDecisionLogs_AiBatchItems_BatchItemId",
                        column: x => x.BatchItemId,
                        principalTable: "AiBatchItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiDecisionLogs_AiBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "AiBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiDecisionLogs_AiJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "AiJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AiReferenceSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    BatchItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceCourseId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceAssignmentExternalId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Role = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CompactSummaryJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiReferenceSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiReferenceSnapshots_AiBatchItems_BatchItemId",
                        column: x => x.BatchItemId,
                        principalTable: "AiBatchItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiReferenceSnapshots_AiBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "AiBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiReferenceSnapshots_AiJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "AiJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiJobs_ParentJobId",
                table: "AiJobs",
                column: "ParentJobId");

            migrationBuilder.CreateIndex(
                name: "IX_AiGeneratedAssignmentDrafts_BatchId",
                table: "AiGeneratedAssignmentDrafts",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_AiGeneratedAssignmentDrafts_BatchItemId",
                table: "AiGeneratedAssignmentDrafts",
                column: "BatchItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiGeneratedAssignmentDrafts_ParentJobId",
                table: "AiGeneratedAssignmentDrafts",
                column: "ParentJobId");

            migrationBuilder.CreateIndex(
                name: "IX_AiArtifacts_DraftId",
                table: "AiArtifacts",
                column: "DraftId");

            migrationBuilder.CreateIndex(
                name: "IX_AiArtifacts_JobId",
                table: "AiArtifacts",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_AiBatches_CourseId",
                table: "AiBatches",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_AiBatches_CreatedByUserId",
                table: "AiBatches",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AiBatchItems_BatchId_Index",
                table: "AiBatchItems",
                columns: new[] { "BatchId", "Index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiDecisionLogs_BatchId",
                table: "AiDecisionLogs",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_AiDecisionLogs_BatchItemId",
                table: "AiDecisionLogs",
                column: "BatchItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AiDecisionLogs_JobId",
                table: "AiDecisionLogs",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_AiReferenceSnapshots_BatchId",
                table: "AiReferenceSnapshots",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_AiReferenceSnapshots_BatchItemId",
                table: "AiReferenceSnapshots",
                column: "BatchItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AiReferenceSnapshots_JobId",
                table: "AiReferenceSnapshots",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_AiReviewFindings_ArtifactId",
                table: "AiReviewFindings",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_AiReviewFindings_DraftId",
                table: "AiReviewFindings",
                column: "DraftId");

            migrationBuilder.AddForeignKey(
                name: "FK_AiGeneratedAssignmentDrafts_AiBatchItems_BatchItemId",
                table: "AiGeneratedAssignmentDrafts",
                column: "BatchItemId",
                principalTable: "AiBatchItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AiGeneratedAssignmentDrafts_AiBatches_BatchId",
                table: "AiGeneratedAssignmentDrafts",
                column: "BatchId",
                principalTable: "AiBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AiGeneratedAssignmentDrafts_AiJobs_ParentJobId",
                table: "AiGeneratedAssignmentDrafts",
                column: "ParentJobId",
                principalTable: "AiJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AiJobs_AiJobs_ParentJobId",
                table: "AiJobs",
                column: "ParentJobId",
                principalTable: "AiJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AiGeneratedAssignmentDrafts_AiBatchItems_BatchItemId",
                table: "AiGeneratedAssignmentDrafts");

            migrationBuilder.DropForeignKey(
                name: "FK_AiGeneratedAssignmentDrafts_AiBatches_BatchId",
                table: "AiGeneratedAssignmentDrafts");

            migrationBuilder.DropForeignKey(
                name: "FK_AiGeneratedAssignmentDrafts_AiJobs_ParentJobId",
                table: "AiGeneratedAssignmentDrafts");

            migrationBuilder.DropForeignKey(
                name: "FK_AiJobs_AiJobs_ParentJobId",
                table: "AiJobs");

            migrationBuilder.DropTable(
                name: "AiDecisionLogs");

            migrationBuilder.DropTable(
                name: "AiReferenceSnapshots");

            migrationBuilder.DropTable(
                name: "AiReviewFindings");

            migrationBuilder.DropTable(
                name: "AiBatchItems");

            migrationBuilder.DropTable(
                name: "AiArtifacts");

            migrationBuilder.DropTable(
                name: "AiBatches");

            migrationBuilder.DropIndex(
                name: "IX_AiJobs_ParentJobId",
                table: "AiJobs");

            migrationBuilder.DropIndex(
                name: "IX_AiGeneratedAssignmentDrafts_BatchId",
                table: "AiGeneratedAssignmentDrafts");

            migrationBuilder.DropIndex(
                name: "IX_AiGeneratedAssignmentDrafts_BatchItemId",
                table: "AiGeneratedAssignmentDrafts");

            migrationBuilder.DropIndex(
                name: "IX_AiGeneratedAssignmentDrafts_ParentJobId",
                table: "AiGeneratedAssignmentDrafts");

            migrationBuilder.DropColumn(
                name: "ParentJobId",
                table: "AiJobs");

            migrationBuilder.DropColumn(
                name: "StageCode",
                table: "AiJobs");

            migrationBuilder.DropColumn(
                name: "StageLabel",
                table: "AiJobs");

            migrationBuilder.DropColumn(
                name: "StageOrder",
                table: "AiJobs");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "AiGeneratedAssignmentDrafts");

            migrationBuilder.DropColumn(
                name: "BatchItemId",
                table: "AiGeneratedAssignmentDrafts");

            migrationBuilder.DropColumn(
                name: "ParentJobId",
                table: "AiGeneratedAssignmentDrafts");
        }
    }
}
