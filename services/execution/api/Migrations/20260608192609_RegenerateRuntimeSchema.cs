using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Execution.Api.Migrations
{
    /// <inheritdoc />
    public partial class RegenerateRuntimeSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExecutionJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Input = table.Column<string>(type: "text", nullable: true),
                    TestsJson = table.Column<string>(type: "text", nullable: true),
                    CodeForbiddenCallsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CodeRequiredCallsJson = table.Column<string>(type: "jsonb", nullable: true),
                    TimeLimitMs = table.Column<int>(type: "integer", nullable: true),
                    MemoryLimitMb = table.Column<int>(type: "integer", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Stdout = table.Column<string>(type: "text", nullable: true),
                    Stderr = table.Column<string>(type: "text", nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunnerHeartbeats",
                columns: table => new
                {
                    RunnerId = table.Column<string>(type: "text", nullable: false),
                    Language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Region = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunnerHeartbeats", x => x.RunnerId);
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

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionJobs_Status_CreatedAt",
                table: "ExecutionJobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionJobs_SubmissionId",
                table: "ExecutionJobs",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionResults_JobId",
                table: "ExecutionResults",
                column: "JobId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExecutionJobs");

            migrationBuilder.DropTable(
                name: "ExecutionResults");

            migrationBuilder.DropTable(
                name: "RunnerHeartbeats");

            migrationBuilder.DropTable(
                name: "ServiceSchemaMarkers");
        }
    }
}
