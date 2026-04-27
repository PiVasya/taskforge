using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class AiDraftRobustness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskAssignments_SourceAgentArtifactId",
                table: "TaskAssignments");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignments_SourceAgentArtifactId",
                table: "TaskAssignments",
                column: "SourceAgentArtifactId",
                unique: true,
                filter: "\"SourceAgentArtifactId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignments_SourceAgentRunId_SourceAgentTaskIndex",
                table: "TaskAssignments",
                columns: new[] { "SourceAgentRunId", "SourceAgentTaskIndex" },
                unique: true,
                filter: "\"SourceAgentRunId\" IS NOT NULL AND \"SourceAgentTaskIndex\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskAssignments_SourceAgentRunId_SourceAgentTaskIndex",
                table: "TaskAssignments");

            migrationBuilder.DropIndex(
                name: "IX_TaskAssignments_SourceAgentArtifactId",
                table: "TaskAssignments");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignments_SourceAgentArtifactId",
                table: "TaskAssignments",
                column: "SourceAgentArtifactId");
        }
    }
}
