using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Tasks.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskGraphImportOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskGraphImportOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreparedPayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResponseJson = table.Column<string>(type: "jsonb", nullable: true),
                    MapRequestJson = table.Column<string>(type: "jsonb", nullable: true),
                    MapResponseJson = table.Column<string>(type: "jsonb", nullable: true),
                    LastErrorJson = table.Column<string>(type: "jsonb", nullable: true),
                    AuditJson = table.Column<string>(type: "jsonb", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskGraphImportOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskGraphImportOperations_CourseId_UserId_UpdatedAt",
                table: "TaskGraphImportOperations",
                columns: new[] { "CourseId", "UserId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskGraphImportOperations");
        }
    }
}
