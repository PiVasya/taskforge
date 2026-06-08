using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Tasks.Api.Migrations
{
    /// <inheritdoc />
    public partial class RegenerateRuntimeSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AllowedLanguagesCsv = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Tags = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Difficulty = table.Column<int>(type: "integer", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    StarterCode = table.Column<string>(type: "text", nullable: true),
                    TestsJson = table.Column<string>(type: "text", nullable: true),
                    CodeForbiddenCallsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CodeRequiredCallsJson = table.Column<string>(type: "jsonb", nullable: true),
                    IsVisible = table.Column<bool>(type: "boolean", nullable: false),
                    Sort = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceSchemaMarkers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceSchemaMarkers", x => x.Id);
                });

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
                name: "IX_Assignments_CourseId_Sort",
                table: "Assignments",
                columns: new[] { "CourseId", "Sort" });

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
                name: "Assignments");

            migrationBuilder.DropTable(
                name: "ServiceSchemaMarkers");

            migrationBuilder.DropTable(
                name: "TaskAttempts");
        }
    }
}
