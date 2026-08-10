using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Education.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCourseStudentVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsHiddenFromStudents",
                table: "Courses",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsHiddenFromStudents",
                table: "Courses");
        }
    }
}
