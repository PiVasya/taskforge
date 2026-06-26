using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Support.Api.Migrations
{
    /// <inheritdoc />
    public partial class SupportSingleChatReplyMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReplyToMessageId",
                table: "SupportMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportMessages_ReplyToMessageId",
                table: "SupportMessages",
                column: "ReplyToMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupportMessages_ReplyToMessageId",
                table: "SupportMessages");

            migrationBuilder.DropColumn(
                name: "ReplyToMessageId",
                table: "SupportMessages");
        }
    }
}
