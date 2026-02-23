using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class Judge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonDocument>(
                name: "CodeForbiddenCallsJson",
                table: "TaskAssignments",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "CodeRequiredCallsJson",
                table: "TaskAssignments",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CodeForbiddenCallsJson",
                table: "TaskAssignments");

            migrationBuilder.DropColumn(
                name: "CodeRequiredCallsJson",
                table: "TaskAssignments");
        }
    }
}
