using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class AddBadgesAndUserBadges2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserBadges_UserId_BadgeId",
                table: "UserBadges");

            migrationBuilder.AddColumn<Guid>(
                name: "BadgeId1",
                table: "UserBadges",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId1",
                table: "UserBadges",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserBadges_BadgeId1",
                table: "UserBadges",
                column: "BadgeId1");

            migrationBuilder.CreateIndex(
                name: "IX_UserBadges_UserId",
                table: "UserBadges",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserBadges_UserId1",
                table: "UserBadges",
                column: "UserId1");

            migrationBuilder.AddForeignKey(
                name: "FK_UserBadges_Badges_BadgeId1",
                table: "UserBadges",
                column: "BadgeId1",
                principalTable: "Badges",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_UserBadges_Users_UserId1",
                table: "UserBadges",
                column: "UserId1",
                principalTable: "Users",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserBadges_Badges_BadgeId1",
                table: "UserBadges");

            migrationBuilder.DropForeignKey(
                name: "FK_UserBadges_Users_UserId1",
                table: "UserBadges");

            migrationBuilder.DropIndex(
                name: "IX_UserBadges_BadgeId1",
                table: "UserBadges");

            migrationBuilder.DropIndex(
                name: "IX_UserBadges_UserId",
                table: "UserBadges");

            migrationBuilder.DropIndex(
                name: "IX_UserBadges_UserId1",
                table: "UserBadges");

            migrationBuilder.DropColumn(
                name: "BadgeId1",
                table: "UserBadges");

            migrationBuilder.DropColumn(
                name: "UserId1",
                table: "UserBadges");

            migrationBuilder.CreateIndex(
                name: "IX_UserBadges_UserId_BadgeId",
                table: "UserBadges",
                columns: new[] { "UserId", "BadgeId" },
                unique: true);
        }
    }
}
