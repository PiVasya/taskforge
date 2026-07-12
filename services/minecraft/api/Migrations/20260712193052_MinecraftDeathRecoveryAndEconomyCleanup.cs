using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Minecraft.Api.Migrations
{
    /// <inheritdoc />
    public partial class MinecraftDeathRecoveryAndEconomyCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Author",
                table: "MinecraftChatMessages");

            migrationBuilder.RenameColumn(
                name: "Text",
                table: "MinecraftChatMessages",
                newName: "Message");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "MinecraftChatMessages",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameIndex(
                name: "IX_MinecraftChatMessages_CreatedAt",
                table: "MinecraftChatMessages",
                newName: "IX_MinecraftChatMessages_CreatedAtUtc");

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "MinecraftLinks",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmedAtUtc",
                table: "MinecraftLinks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UnlinkedAtUtc",
                table: "MinecraftLinks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorName",
                table: "MinecraftChatMessages",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MinecraftNick",
                table: "MinecraftChatMessages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MinecraftUuid",
                table: "MinecraftChatMessages",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "MinecraftChatMessages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "MinecraftChatMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MinecraftEconomySettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WeeklyPenalty = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinecraftLinkCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MinecraftRatingTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    PlayerUuid = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Delta = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RequestId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinecraftRatingTransactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MinecraftWeeklyJoins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WeekStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PenaltyApplied = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinecraftWeeklyJoins", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftLinks_UserId_Confirmed_UnlinkedAtUtc",
                table: "MinecraftLinks",
                columns: new[] { "UserId", "Confirmed", "UnlinkedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftEconomySettings_UpdatedAtUtc",
                table: "MinecraftEconomySettings",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftLinkCodes_UserId_ExpiresAtUtc_UsedAtUtc",
                table: "MinecraftLinkCodes",
                columns: new[] { "UserId", "ExpiresAtUtc", "UsedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftRatingTransactions_RequestId",
                table: "MinecraftRatingTransactions",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftRatingTransactions_UserId_CreatedAtUtc",
                table: "MinecraftRatingTransactions",
                columns: new[] { "UserId", "CreatedAtUtc" });

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
                name: "MinecraftRatingTransactions");

            migrationBuilder.DropTable(
                name: "MinecraftWeeklyJoins");

            migrationBuilder.DropIndex(
                name: "IX_MinecraftLinks_UserId_Confirmed_UnlinkedAtUtc",
                table: "MinecraftLinks");

            migrationBuilder.DropColumn(
                name: "ConfirmedAtUtc",
                table: "MinecraftLinks");

            migrationBuilder.DropColumn(
                name: "UnlinkedAtUtc",
                table: "MinecraftLinks");

            migrationBuilder.DropColumn(
                name: "AuthorName",
                table: "MinecraftChatMessages");

            migrationBuilder.DropColumn(
                name: "MinecraftNick",
                table: "MinecraftChatMessages");

            migrationBuilder.DropColumn(
                name: "MinecraftUuid",
                table: "MinecraftChatMessages");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "MinecraftChatMessages");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "MinecraftChatMessages");

            migrationBuilder.RenameColumn(
                name: "Message",
                table: "MinecraftChatMessages",
                newName: "Text");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                table: "MinecraftChatMessages",
                newName: "CreatedAt");

            migrationBuilder.RenameIndex(
                name: "IX_MinecraftChatMessages_CreatedAtUtc",
                table: "MinecraftChatMessages",
                newName: "IX_MinecraftChatMessages_CreatedAt");

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "MinecraftLinks",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);

            migrationBuilder.AddColumn<string>(
                name: "Author",
                table: "MinecraftChatMessages",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");
        }
    }
}
