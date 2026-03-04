using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class AddMinecraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinecraftLinkCount",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "MinecraftLinkedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MinecraftNick",
                table: "Users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MinecraftUuid",
                table: "Users",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MinecraftEconomySettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WeeklyPenalty = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinecraftEconomySettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MinecraftLinkCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nick = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Salt = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinecraftLinkCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MinecraftLinkCodes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MinecraftWeeklyJoins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WeekStartUtc = table.Column<DateTime>(type: "date", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinecraftWeeklyJoins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MinecraftWeeklyJoins_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftLinkCodes_UserId_Nick",
                table: "MinecraftLinkCodes",
                columns: new[] { "UserId", "Nick" });

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftWeeklyJoins_UserId_WeekStartUtc",
                table: "MinecraftWeeklyJoins",
                columns: new[] { "UserId", "WeekStartUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MinecraftEconomySettings");

            migrationBuilder.DropTable(
                name: "MinecraftLinkCodes");

            migrationBuilder.DropTable(
                name: "MinecraftWeeklyJoins");

            migrationBuilder.DropColumn(
                name: "MinecraftLinkCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MinecraftLinkedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MinecraftNick",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MinecraftUuid",
                table: "Users");
        }
    }
}
