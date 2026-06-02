using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Solutions.Api.Migrations
{
    /// <inheritdoc />
    public partial class CompileCheck_20260601_233749 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Badges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Badges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserBadges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BadgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    AwardedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserBadges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserImageTaskSolutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    SimilarityPercent = table.Column<int>(type: "integer", nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserImageTaskSolutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserQuotaBuckets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BucketType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Tokens = table.Column<int>(type: "integer", nullable: false),
                    LastRefillAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserQuotaBuckets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserBadges_UserId_BadgeId",
                table: "UserBadges",
                columns: new[] { "UserId", "BadgeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserImageTaskSolutions_AssignmentId",
                table: "UserImageTaskSolutions",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_UserImageTaskSolutions_UserId_CreatedAt",
                table: "UserImageTaskSolutions",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserQuotaBuckets_UserId_BucketType",
                table: "UserQuotaBuckets",
                columns: new[] { "UserId", "BucketType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Badges");

            migrationBuilder.DropTable(
                name: "UserBadges");

            migrationBuilder.DropTable(
                name: "UserImageTaskSolutions");

            migrationBuilder.DropTable(
                name: "UserQuotaBuckets");
        }
    }
}
