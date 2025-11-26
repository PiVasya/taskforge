using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class AddBadgesAndUserBadges3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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
                name: "IX_UserBadges_UserId1",
                table: "UserBadges");

            migrationBuilder.DropColumn(
                name: "BadgeId1",
                table: "UserBadges");

            migrationBuilder.DropColumn(
                name: "UserId1",
                table: "UserBadges");

            migrationBuilder.AlterColumn<string>(
                name: "ImageUrl",
                table: "Badges",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.AlterColumn<string>(
                name: "ImageUrl",
                table: "Badges",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8192)",
                oldMaxLength: 8192);

            migrationBuilder.CreateIndex(
                name: "IX_UserBadges_BadgeId1",
                table: "UserBadges",
                column: "BadgeId1");

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
    }
}
