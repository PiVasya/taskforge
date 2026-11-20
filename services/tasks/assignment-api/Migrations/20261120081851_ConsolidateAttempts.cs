using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Tasks.Api.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskAttempts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnswersJson = table.Column<string>(type: "jsonb", nullable: true),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    CorrectUnits = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EarnedScore = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OrderJson = table.Column<string>(type: "jsonb", nullable: true),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewJson = table.Column<string>(type: "jsonb", nullable: true),
                    ScorePercent = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TaskAssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimeExpired = table.Column<bool>(type: "boolean", nullable: false),
                    TimeLimitSeconds = table.Column<int>(type: "integer", nullable: true),
                    TotalScore = table.Column<int>(type: "integer", nullable: false),
                    TotalUnits = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskAttempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttempts_TaskAssignmentId_Kind_AttemptNumber",
                table: "TaskAttempts",
                columns: new[] { "TaskAssignmentId", "Kind", "AttemptNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttempts_UserId_Kind_TaskAssignmentId_SubmittedAt",
                table: "TaskAttempts",
                columns: new[] { "UserId", "Kind", "TaskAssignmentId", "SubmittedAt" });
        }
    }
}
