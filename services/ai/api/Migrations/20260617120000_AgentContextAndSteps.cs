using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Ai.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgentContextAndSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignmentId",
                table: "AiConversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CourseId",
                table: "AiConversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupportTicketId",
                table: "AiConversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RunId",
                table: "AiMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ActionName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    DataJson = table.Column<string>(type: "jsonb", nullable: true),
                    IsVisibleToUser = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiSteps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiMessages_RunId",
                table: "AiMessages",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_AiConversations_AssignmentId",
                table: "AiConversations",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AiConversations_CourseId",
                table: "AiConversations",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_AiSteps_ConversationId_CreatedAtUtc",
                table: "AiSteps",
                columns: new[] { "ConversationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiSteps_RunId_Seq",
                table: "AiSteps",
                columns: new[] { "RunId", "Seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiSteps");

            migrationBuilder.DropIndex(
                name: "IX_AiConversations_AssignmentId",
                table: "AiConversations");

            migrationBuilder.DropIndex(
                name: "IX_AiConversations_CourseId",
                table: "AiConversations");

            migrationBuilder.DropIndex(
                name: "IX_AiMessages_RunId",
                table: "AiMessages");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "AiMessages");

            migrationBuilder.DropColumn(
                name: "AssignmentId",
                table: "AiConversations");

            migrationBuilder.DropColumn(
                name: "CourseId",
                table: "AiConversations");

            migrationBuilder.DropColumn(
                name: "SupportTicketId",
                table: "AiConversations");
        }
    }
}
