using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TelegramChatId",
                table: "Users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TelegramLinkCount",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "TelegramLinkedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TelegramUsername",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TelegramLinkCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Salt = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramLinkCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelegramLinkCodes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_TelegramChatId",
                table: "Users",
                column: "TelegramChatId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramLinkCodes_UserId",
                table: "TelegramLinkCodes",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramLinkCodes");

            migrationBuilder.DropIndex(
                name: "IX_Users_TelegramChatId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TelegramChatId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TelegramLinkCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TelegramLinkedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TelegramUsername",
                table: "Users");
        }
    }
}
