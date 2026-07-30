using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Ai.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountIntelligenceManager : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountAnalysisFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    FindingKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    ModelProbability = table.Column<double>(type: "double precision", nullable: false),
                    PrimaryUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecondaryUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SuggestedPrimaryUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DataJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAnalysisFindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountAnalysisReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SubjectKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OtherUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Decision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SignalsJson = table.Column<string>(type: "jsonb", nullable: true),
                    DataJson = table.Column<string>(type: "jsonb", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAnalysisReviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountAnalysisRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Phase = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    TotalAccounts = table.Column<int>(type: "integer", nullable: false),
                    CandidatePairs = table.Column<int>(type: "integer", nullable: false),
                    DuplicateFindings = table.Column<int>(type: "integer", nullable: false),
                    SuspiciousFindings = table.Column<int>(type: "integer", nullable: false),
                    AlgorithmVersion = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SourcesJson = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorJson = table.Column<string>(type: "jsonb", nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAnalysisRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAnalysisFindings_PrimaryUserId_SecondaryUserId",
                table: "AccountAnalysisFindings",
                columns: new[] { "PrimaryUserId", "SecondaryUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAnalysisFindings_RunId_FindingKey",
                table: "AccountAnalysisFindings",
                columns: new[] { "RunId", "FindingKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountAnalysisFindings_RunId_Kind_Score",
                table: "AccountAnalysisFindings",
                columns: new[] { "RunId", "Kind", "Score" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAnalysisReviews_SubjectType_SubjectKey",
                table: "AccountAnalysisReviews",
                columns: new[] { "SubjectType", "SubjectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountAnalysisReviews_UserId",
                table: "AccountAnalysisReviews",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountAnalysisRuns_Status_CreatedAtUtc",
                table: "AccountAnalysisRuns",
                columns: new[] { "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountAnalysisFindings");

            migrationBuilder.DropTable(
                name: "AccountAnalysisReviews");

            migrationBuilder.DropTable(
                name: "AccountAnalysisRuns");
        }
    }
}
