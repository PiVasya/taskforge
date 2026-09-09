using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Execution.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSqlDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "SubmissionId",
                table: "ExecutionJobs",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "ClaimedByWorkerId",
                table: "ExecutionJobs",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeduplicationKey",
                table: "ExecutionJobs",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "ExecutionJobs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "ExecutionJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseToken",
                table: "ExecutionJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayloadJson",
                table: "ExecutionJobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PayloadVersion",
                table: "ExecutionJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Target",
                table: "ExecutionJobs",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionJobs_DeduplicationKey",
                table: "ExecutionJobs",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionJobs_Status_Kind_Target_CreatedAt",
                table: "ExecutionJobs",
                columns: new[] { "Status", "Kind", "Target", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionJobs_Status_LeaseExpiresAt",
                table: "ExecutionJobs",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExecutionJobs_Kind",
                table: "ExecutionJobs",
                sql: "\"Kind\" IN ('legacy', 'code', 'image', 'sql-check', 'sql-preview', 'sql-materialize')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExecutionJobs_Lease",
                table: "ExecutionJobs",
                sql: "(\"ClaimedByWorkerId\" IS NULL AND \"LeaseToken\" IS NULL AND \"LeaseExpiresAt\" IS NULL) OR (\"ClaimedByWorkerId\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExecutionJobs_Payload",
                table: "ExecutionJobs",
                sql: "(\"Kind\" = 'legacy' AND \"PayloadVersion\" IS NULL AND \"PayloadJson\" IS NULL) OR (\"Kind\" <> 'legacy' AND \"Target\" IS NOT NULL AND length(\"Target\") > 0 AND \"PayloadVersion\" IS NOT NULL AND \"PayloadVersion\" > 0 AND \"PayloadJson\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExecutionJobs_SqlNoLegacyTests",
                table: "ExecutionJobs",
                sql: "\"Kind\" NOT IN ('sql-check', 'sql-preview', 'sql-materialize') OR (\"TestsJson\" IS NULL AND \"CodeForbiddenCallsJson\" IS NULL AND \"CodeRequiredCallsJson\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExecutionJobs_SqlSubmission",
                table: "ExecutionJobs",
                sql: "(\"Kind\" NOT IN ('sql-preview', 'sql-materialize') OR \"SubmissionId\" IS NULL) AND (\"Kind\" <> 'sql-check' OR (\"SubmissionId\" IS NOT NULL AND \"AssignmentId\" IS NOT NULL AND \"UserId\" IS NOT NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExecutionJobs_DeduplicationKey",
                table: "ExecutionJobs");

            migrationBuilder.DropIndex(
                name: "IX_ExecutionJobs_Status_Kind_Target_CreatedAt",
                table: "ExecutionJobs");

            migrationBuilder.DropIndex(
                name: "IX_ExecutionJobs_Status_LeaseExpiresAt",
                table: "ExecutionJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExecutionJobs_Kind",
                table: "ExecutionJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExecutionJobs_Lease",
                table: "ExecutionJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExecutionJobs_Payload",
                table: "ExecutionJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExecutionJobs_SqlNoLegacyTests",
                table: "ExecutionJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExecutionJobs_SqlSubmission",
                table: "ExecutionJobs");

            migrationBuilder.DropColumn(
                name: "ClaimedByWorkerId",
                table: "ExecutionJobs");

            migrationBuilder.DropColumn(
                name: "DeduplicationKey",
                table: "ExecutionJobs");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ExecutionJobs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "ExecutionJobs");

            migrationBuilder.DropColumn(
                name: "LeaseToken",
                table: "ExecutionJobs");

            migrationBuilder.DropColumn(
                name: "PayloadJson",
                table: "ExecutionJobs");

            migrationBuilder.DropColumn(
                name: "PayloadVersion",
                table: "ExecutionJobs");

            migrationBuilder.DropColumn(
                name: "Target",
                table: "ExecutionJobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "SubmissionId",
                table: "ExecutionJobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
