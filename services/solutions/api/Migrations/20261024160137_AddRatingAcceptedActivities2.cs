using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Solutions.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRatingAcceptedActivities2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RatingAcceptedActivities",
                columns: table => new
                {
                    SourceKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatingAcceptedActivities", x => new { x.SourceKind, x.SourceId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_RatingAcceptedActivities_CourseId_SubmittedAt",
                table: "RatingAcceptedActivities",
                columns: new[] { "CourseId", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RatingAcceptedActivities_SubmittedAt",
                table: "RatingAcceptedActivities",
                column: "SubmittedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RatingAcceptedActivities_UserId_AssignmentId",
                table: "RatingAcceptedActivities",
                columns: new[] { "UserId", "AssignmentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RatingAcceptedActivities");
        }
    }
}
