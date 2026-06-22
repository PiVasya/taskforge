using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Support.Api.Migrations
{
    /// <inheritdoc />
    public partial class SupportBotTelegramIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "SupportMessages",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TelegramChatId",
                table: "SupportMessages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TelegramMessageId",
                table: "SupportMessages",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportMessages_TelegramChatId_TelegramMessageId",
                table: "SupportMessages",
                columns: new[] { "TelegramChatId", "TelegramMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportMessages_TelegramMessageId",
                table: "SupportMessages",
                column: "TelegramMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupportMessages_TelegramChatId_TelegramMessageId",
                table: "SupportMessages");

            migrationBuilder.DropIndex(
                name: "IX_SupportMessages_TelegramMessageId",
                table: "SupportMessages");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "SupportMessages");

            migrationBuilder.DropColumn(
                name: "TelegramChatId",
                table: "SupportMessages");

            migrationBuilder.DropColumn(
                name: "TelegramMessageId",
                table: "SupportMessages");
        }
    }
}
