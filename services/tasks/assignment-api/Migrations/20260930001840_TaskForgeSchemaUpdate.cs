using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Tasks.Api.Migrations
{
    /// <inheritdoc />
    public partial class TaskForgeSchemaUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CodeAssignmentSpecs",
                columns: table => new
                {
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AllowedLanguagesCsv = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    StarterCode = table.Column<string>(type: "text", nullable: true),
                    TestsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CodeForbiddenCallsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CodeRequiredCallsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodeAssignmentSpecs", x => x.AssignmentId);
                    table.ForeignKey(
                        name: "FK_CodeAssignmentSpecs_Assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "Assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImageAssignmentSpecs",
                columns: table => new
                {
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AllowedLanguagesCsv = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    StarterCode = table.Column<string>(type: "text", nullable: true),
                    TestsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CodeForbiddenCallsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CodeRequiredCallsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageAssignmentSpecs", x => x.AssignmentId);
                    table.ForeignKey(
                        name: "FK_ImageAssignmentSpecs_Assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "Assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MathAssignmentSpecs",
                columns: table => new
                {
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SettingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    BlocksJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MathAssignmentSpecs", x => x.AssignmentId);
                    table.ForeignKey(
                        name: "FK_MathAssignmentSpecs_Assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "Assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestAssignmentSpecs",
                columns: table => new
                {
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SettingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    QuestionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestAssignmentSpecs", x => x.AssignmentId);
                    table.ForeignKey(
                        name: "FK_TestAssignmentSpecs_Assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "Assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CodeAssignmentSpecs");

            migrationBuilder.DropTable(
                name: "ImageAssignmentSpecs");

            migrationBuilder.DropTable(
                name: "MathAssignmentSpecs");

            migrationBuilder.DropTable(
                name: "TestAssignmentSpecs");
        }
    }
}
