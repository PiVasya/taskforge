using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class AddHiddenAiDraftAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskAssignments_CourseId",
                table: "TaskAssignments");

            migrationBuilder.AddColumn<string>(
                name: "AiDraftJson",
                table: "TaskAssignments",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAiDraft",
                table: "TaskAssignments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "TaskAssignments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LifecycleStatus",
                table: "TaskAssignments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "published");

            migrationBuilder.AddColumn<DateTime>(
                name: "PolishedAtUtc",
                table: "TaskAssignments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAtUtc",
                table: "TaskAssignments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceAgentArtifactId",
                table: "TaskAssignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceAgentRunId",
                table: "TaskAssignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceAgentTaskIndex",
                table: "TaskAssignments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignments_CourseId_IsHidden_LifecycleStatus_Sort",
                table: "TaskAssignments",
                columns: new[] { "CourseId", "IsHidden", "LifecycleStatus", "Sort" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignments_SourceAgentArtifactId",
                table: "TaskAssignments",
                column: "SourceAgentArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignments_SourceAgentRunId",
                table: "TaskAssignments",
                column: "SourceAgentRunId");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskAssignments_AgentRunArtifacts_SourceAgentArtifactId",
                table: "TaskAssignments",
                column: "SourceAgentArtifactId",
                principalTable: "AgentRunArtifacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_TaskAssignments_AgentRuns_SourceAgentRunId",
                table: "TaskAssignments",
                column: "SourceAgentRunId",
                principalTable: "AgentRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskAssignments_AgentRunArtifacts_SourceAgentArtifactId",
                table: "TaskAssignments");

            migrationBuilder.DropForeignKey(
                name: "FK_TaskAssignments_AgentRuns_SourceAgentRunId",
                table: "TaskAssignments");

            migrationBuilder.DropIndex(
                name: "IX_TaskAssignments_CourseId_IsHidden_LifecycleStatus_Sort",
                table: "TaskAssignments");

            migrationBuilder.DropIndex(
                name: "IX_TaskAssignments_SourceAgentArtifactId",
                table: "TaskAssignments");

            migrationBuilder.DropIndex(
                name: "IX_TaskAssignments_SourceAgentRunId",
                table: "TaskAssignments");

            migrationBuilder.DropColumn(
                name: "AiDraftJson",
                table: "TaskAssignments");

            migrationBuilder.DropColumn(
                name: "IsAiDraft",
                table: "TaskAssignments");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "TaskAssignments");

            migrationBuilder.DropColumn(
                name: "LifecycleStatus",
                table: "TaskAssignments");

            migrationBuilder.DropColumn(
                name: "PolishedAtUtc",
                table: "TaskAssignments");

            migrationBuilder.DropColumn(
                name: "PublishedAtUtc",
                table: "TaskAssignments");

            migrationBuilder.DropColumn(
                name: "SourceAgentArtifactId",
                table: "TaskAssignments");

            migrationBuilder.DropColumn(
                name: "SourceAgentRunId",
                table: "TaskAssignments");

            migrationBuilder.DropColumn(
                name: "SourceAgentTaskIndex",
                table: "TaskAssignments");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignments_CourseId",
                table: "TaskAssignments",
                column: "CourseId");
        }
    }
}
