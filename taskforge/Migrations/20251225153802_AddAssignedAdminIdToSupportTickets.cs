using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class AddAssignedAdminIdToSupportTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupportMessages_Users_AuthorId",
                table: "SupportMessages");

            migrationBuilder.DropIndex(
                name: "IX_SupportMessages_AuthorId",
                table: "SupportMessages");

            migrationBuilder.RenameColumn(
                name: "AuthorId",
                table: "SupportMessages",
                newName: "AuthorUserId");

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedAdminId",
                table: "SupportTickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "SupportMessages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TelegramChatId",
                table: "SupportMessages",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedAdminId",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "SupportMessages");

            migrationBuilder.DropColumn(
                name: "TelegramChatId",
                table: "SupportMessages");

            migrationBuilder.RenameColumn(
                name: "AuthorUserId",
                table: "SupportMessages",
                newName: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportMessages_AuthorId",
                table: "SupportMessages",
                column: "AuthorId");

            migrationBuilder.AddForeignKey(
                name: "FK_SupportMessages_Users_AuthorId",
                table: "SupportMessages",
                column: "AuthorId",
                principalTable: "Users",
                principalColumn: "Id");
        }
    }
}
