using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Ai.Api.Migrations
{
    /// <inheritdoc />
    public partial class CompileCheck_20260601_233749 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAtUtc",
                table: "AiRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorJson",
                table: "AiRuns",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobType",
                table: "AiRuns",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PayloadJson",
                table: "AiRuns",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAtUtc",
                table: "AiRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkerId",
                table: "AiRuns",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    DataJson = table.Column<string>(type: "jsonb", nullable: false),
                    Applied = table.Column<bool>(type: "boolean", nullable: false),
                    AppliedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiArtifacts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiRuns_Status_CreatedAtUtc",
                table: "AiRuns",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiArtifacts_ConversationId_CreatedAtUtc",
                table: "AiArtifacts",
                columns: new[] { "ConversationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiArtifacts_RunId_CreatedAtUtc",
                table: "AiArtifacts",
                columns: new[] { "RunId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_AiRuns_Status_CreatedAtUtc",
                table: "AiRuns");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "AiRuns");

            migrationBuilder.DropColumn(
                name: "ErrorJson",
                table: "AiRuns");

            migrationBuilder.DropColumn(
                name: "JobType",
                table: "AiRuns");

            migrationBuilder.DropColumn(
                name: "PayloadJson",
                table: "AiRuns");

            migrationBuilder.DropColumn(
                name: "StartedAtUtc",
                table: "AiRuns");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                table: "AiRuns");
        }
    }
}
