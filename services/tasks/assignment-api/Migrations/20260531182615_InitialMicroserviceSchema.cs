using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Tasks.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialMicroserviceSchema : Migration
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
                    StarterCode = table.Column<string>(type: "text", nullable: true),
                    TestsJson = table.Column<string>(type: "text", nullable: true),
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

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_CourseId_Sort",
                table: "Assignments",
                columns: new[] { "CourseId", "Sort" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Assignments");

            migrationBuilder.DropTable(
                name: "ServiceSchemaMarkers");
        }
    }
}
