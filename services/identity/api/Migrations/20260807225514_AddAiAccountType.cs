using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Identity.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAiAccountType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountType",
                table: "Users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "human");

            migrationBuilder.CreateIndex(
                name: "IX_Users_AccountType",
                table: "Users",
                column: "AccountType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_AccountType",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AccountType",
                table: "Users");
        }
    }
}
