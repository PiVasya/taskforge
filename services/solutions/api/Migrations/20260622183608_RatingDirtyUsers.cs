using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Solutions.Api.Migrations
{
    /// <inheritdoc />
    public partial class RatingDirtyUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RatingDirtyUsers",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    MarkedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatingDirtyUsers", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RatingDirtyUsers_MarkedAtUtc",
                table: "RatingDirtyUsers",
                column: "MarkedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RatingDirtyUsers");
        }
    }
}
