using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class ImageTestFields2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageTestReferenceKey",
                table: "TaskAssignments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ImageTestSimilarityThreshold",
                table: "TaskAssignments",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageTestReferenceKey",
                table: "TaskAssignments");

            migrationBuilder.DropColumn(
                name: "ImageTestSimilarityThreshold",
                table: "TaskAssignments");
        }
    }
}
