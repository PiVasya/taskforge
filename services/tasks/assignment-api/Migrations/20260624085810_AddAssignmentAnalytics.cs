using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Tasks.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAssignmentAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnalyticsSettingsJson",
                table: "Assignments",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssignmentActivityEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EventUid = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClientTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: true),
                    CodeLength = table.Column<int>(type: "integer", nullable: true),
                    CodeDelta = table.Column<int>(type: "integer", nullable: true),
                    CodeHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TextLength = table.Column<int>(type: "integer", nullable: true),
                    TextHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TextSample = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    Language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ActiveDurationMs = table.Column<long>(type: "bigint", nullable: true),
                    HiddenDurationMs = table.Column<long>(type: "bigint", nullable: true),
                    BlurDurationMs = table.Column<long>(type: "bigint", nullable: true),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: true),
                    RiskPoints = table.Column<int>(type: "integer", nullable: false),
                    RiskReason = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    IpHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UserAgentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentActivityEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssignmentCodeSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CodeLength = table.Column<int>(type: "integer", nullable: false),
                    CodeDelta = table.Column<int>(type: "integer", nullable: true),
                    CodeHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CodeSample = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    FullCode = table.Column<string>(type: "text", nullable: true),
                    RelatedEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedSubmissionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentCodeSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssignmentWorkSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TotalDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    ActiveDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    HiddenDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    BlurDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    OpenCount = table.Column<int>(type: "integer", nullable: false),
                    CloseCount = table.Column<int>(type: "integer", nullable: false),
                    HiddenCount = table.Column<int>(type: "integer", nullable: false),
                    VisibleCount = table.Column<int>(type: "integer", nullable: false),
                    BlurCount = table.Column<int>(type: "integer", nullable: false),
                    FocusCount = table.Column<int>(type: "integer", nullable: false),
                    PasteCount = table.Column<int>(type: "integer", nullable: false),
                    CopyCount = table.Column<int>(type: "integer", nullable: false),
                    CutCount = table.Column<int>(type: "integer", nullable: false),
                    CodeChangeCount = table.Column<int>(type: "integer", nullable: false),
                    LanguageChangeCount = table.Column<int>(type: "integer", nullable: false),
                    SubmitCount = table.Column<int>(type: "integer", nullable: false),
                    FailedSubmitCount = table.Column<int>(type: "integer", nullable: false),
                    PassedSubmitCount = table.Column<int>(type: "integer", nullable: false),
                    FullscreenExitCount = table.Column<int>(type: "integer", nullable: false),
                    MaxCodeLength = table.Column<int>(type: "integer", nullable: false),
                    FinalCodeLength = table.Column<int>(type: "integer", nullable: false),
                    FinalCodeHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastLanguage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RiskScore = table.Column<int>(type: "integer", nullable: false),
                    RiskLevel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RiskReasonsJson = table.Column<string>(type: "jsonb", nullable: true),
                    LastEventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentWorkSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentActivityEvents_AssignmentId_CreatedAt",
                table: "AssignmentActivityEvents",
                columns: new[] { "AssignmentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentActivityEvents_AssignmentId_EventType_CreatedAt",
                table: "AssignmentActivityEvents",
                columns: new[] { "AssignmentId", "EventType", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentActivityEvents_AssignmentId_UserId_SessionId_Crea~",
                table: "AssignmentActivityEvents",
                columns: new[] { "AssignmentId", "UserId", "SessionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentActivityEvents_AssignmentId_UserId_SessionId_Even~",
                table: "AssignmentActivityEvents",
                columns: new[] { "AssignmentId", "UserId", "SessionId", "EventUid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCodeSnapshots_AssignmentId_CodeHash",
                table: "AssignmentCodeSnapshots",
                columns: new[] { "AssignmentId", "CodeHash" });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCodeSnapshots_AssignmentId_SessionId_CreatedAt",
                table: "AssignmentCodeSnapshots",
                columns: new[] { "AssignmentId", "SessionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCodeSnapshots_AssignmentId_UserId_CreatedAt",
                table: "AssignmentCodeSnapshots",
                columns: new[] { "AssignmentId", "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentWorkSessions_AssignmentId_LastActivityAt",
                table: "AssignmentWorkSessions",
                columns: new[] { "AssignmentId", "LastActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentWorkSessions_AssignmentId_RiskScore",
                table: "AssignmentWorkSessions",
                columns: new[] { "AssignmentId", "RiskScore" });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentWorkSessions_AssignmentId_UserId_SessionId",
                table: "AssignmentWorkSessions",
                columns: new[] { "AssignmentId", "UserId", "SessionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssignmentActivityEvents");

            migrationBuilder.DropTable(
                name: "AssignmentCodeSnapshots");

            migrationBuilder.DropTable(
                name: "AssignmentWorkSessions");

            migrationBuilder.DropColumn(
                name: "AnalyticsSettingsJson",
                table: "Assignments");
        }
    }
}
