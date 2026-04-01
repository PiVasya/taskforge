using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class AddAiScaffolding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    TargetEntityType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    TargetEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: true),
                    InputJson = table.Column<string>(type: "jsonb", nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorText = table.Column<string>(type: "text", nullable: true),
                    ModelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    WorkerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiJobs_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiJobs_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AiAssignmentInsights",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    SuggestionsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiAssignmentInsights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiAssignmentInsights_AiJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "AiJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiAssignmentInsights_TaskAssignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "TaskAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiGeneratedAssignmentDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignmentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DraftJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiGeneratedAssignmentDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiGeneratedAssignmentDrafts_AiJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "AiJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiGeneratedAssignmentDrafts_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiGeneratedAssignmentDrafts_Users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiGeneratedAssignmentDrafts_Users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AiJobFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    OriginalName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MimeType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PublicUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiJobFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiJobFiles_AiJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "AiJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiSubmissionReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    SourceAttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    Verdict = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    SignalsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiSubmissionReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiSubmissionReviews_AiJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "AiJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiSubmissionReviews_TaskAssignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "TaskAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiSubmissionReviews_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AiUserRiskReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RiskLevel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    SignalsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUserRiskReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiUserRiskReports_AiJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "AiJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiUserRiskReports_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiAssignmentInsights_AssignmentId",
                table: "AiAssignmentInsights",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AiAssignmentInsights_JobId",
                table: "AiAssignmentInsights",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_AiGeneratedAssignmentDrafts_CourseId",
                table: "AiGeneratedAssignmentDrafts",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_AiGeneratedAssignmentDrafts_JobId",
                table: "AiGeneratedAssignmentDrafts",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiGeneratedAssignmentDrafts_RequestedByUserId",
                table: "AiGeneratedAssignmentDrafts",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AiGeneratedAssignmentDrafts_ReviewedByUserId",
                table: "AiGeneratedAssignmentDrafts",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AiJobFiles_JobId",
                table: "AiJobFiles",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_AiJobs_CourseId",
                table: "AiJobs",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_AiJobs_CreatedByUserId",
                table: "AiJobs",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AiJobs_Status_Priority_CreatedAtUtc",
                table: "AiJobs",
                columns: new[] { "Status", "Priority", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiSubmissionReviews_AssignmentId",
                table: "AiSubmissionReviews",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AiSubmissionReviews_JobId",
                table: "AiSubmissionReviews",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_AiSubmissionReviews_UserId",
                table: "AiSubmissionReviews",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AiUserRiskReports_JobId",
                table: "AiUserRiskReports",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_AiUserRiskReports_UserId",
                table: "AiUserRiskReports",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiAssignmentInsights");

            migrationBuilder.DropTable(
                name: "AiGeneratedAssignmentDrafts");

            migrationBuilder.DropTable(
                name: "AiJobFiles");

            migrationBuilder.DropTable(
                name: "AiSubmissionReviews");

            migrationBuilder.DropTable(
                name: "AiUserRiskReports");

            migrationBuilder.DropTable(
                name: "AiJobs");
        }
    }
}
