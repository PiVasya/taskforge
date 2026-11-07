using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Support.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordSupportMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscordMessageId",
                table: "SupportMessages",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportMessages_DiscordMessageId",
                table: "SupportMessages",
                column: "DiscordMessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupportMessages_DiscordMessageId",
                table: "SupportMessages");

            migrationBuilder.DropColumn(
                name: "DiscordMessageId",
                table: "SupportMessages");
        }
    }
}
