using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Identity.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountLifecycleManager : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountStatus",
                table: "Users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedByUserId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                table: "Users",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MergedIntoUserId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BlockedAccounts",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    BlockedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedAccounts", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_AccountStatus",
                table: "Users",
                column: "AccountStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Users_MergedIntoUserId",
                table: "Users",
                column: "MergedIntoUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedAccounts_BlockedAtUtc",
                table: "BlockedAccounts",
                column: "BlockedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedAccounts_ExpiresAtUtc",
                table: "BlockedAccounts",
                column: "ExpiresAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockedAccounts");

            migrationBuilder.DropIndex(
                name: "IX_Users_AccountStatus",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_MergedIntoUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AccountStatus",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MergedIntoUserId",
                table: "Users");
        }
    }
}
