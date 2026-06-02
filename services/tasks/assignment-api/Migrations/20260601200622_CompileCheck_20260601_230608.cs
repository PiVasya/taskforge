using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Tasks.Api.Migrations
{
    /// <inheritdoc />
    public partial class CompileCheck_20260601_230608 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TaskAssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TimeLimitSeconds = table.Column<int>(type: "integer", nullable: true),
                    TimeExpired = table.Column<bool>(type: "boolean", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ScorePercent = table.Column<int>(type: "integer", nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    TotalUnits = table.Column<int>(type: "integer", nullable: false),
                    CorrectUnits = table.Column<int>(type: "integer", nullable: false),
                    TotalScore = table.Column<int>(type: "integer", nullable: false),
                    EarnedScore = table.Column<int>(type: "integer", nullable: false),
                    OrderJson = table.Column<string>(type: "jsonb", nullable: true),
                    AnswersJson = table.Column<string>(type: "jsonb", nullable: true),
                    ReviewJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskAttempts");
        }
    }
}
