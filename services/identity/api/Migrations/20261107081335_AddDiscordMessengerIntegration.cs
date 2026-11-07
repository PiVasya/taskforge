using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Identity.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordMessengerIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DiscordLinkCount",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DiscordLinkedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscordName",
                table: "Users",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscordUserId",
                table: "Users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrimaryMessenger",
                table: "Users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DiscordLinkCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordLinkCodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_DiscordUserId",
                table: "Users",
                column: "DiscordUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordLinkCodes_CodeHash",
                table: "DiscordLinkCodes",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordLinkCodes_UserId",
                table: "DiscordLinkCodes",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscordLinkCodes");

            migrationBuilder.DropIndex(
                name: "IX_Users_DiscordUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DiscordLinkCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DiscordLinkedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DiscordName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DiscordUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PrimaryMessenger",
                table: "Users");
        }
    }
}
