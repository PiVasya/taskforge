using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Education.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCourseHierarchyAndSort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentCourseId",
                table: "Courses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Sort",
                table: "Courses",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Courses_ParentCourseId_Sort",
                table: "Courses",
                columns: new[] { "ParentCourseId", "Sort" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Courses_ParentCourseId_Sort",
                table: "Courses");

            migrationBuilder.DropColumn(
                name: "ParentCourseId",
                table: "Courses");

            migrationBuilder.DropColumn(
                name: "Sort",
                table: "Courses");
        }
    }
}
