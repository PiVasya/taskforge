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
                defaultValue: "SiteUser");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "MinecraftChatMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MinecraftDeathRecoveries",
                columns: table => new
                {
                    DeathId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlayerUuid = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WorldUuid = table.Column<Guid>(type: "uuid", nullable: false),
                    WorldKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    WorldName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    X = table.Column<double>(type: "double precision", nullable: false),
                    Y = table.Column<double>(type: "double precision", nullable: false),
                    Z = table.Column<double>(type: "double precision", nullable: false),
                    Yaw = table.Column<float>(type: "real", nullable: false),
                    Pitch = table.Column<float>(type: "real", nullable: false),
                    ItemsPayload = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OfferExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PurchaseRequestId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ChargedAmount = table.Column<int>(type: "integer", nullable: false),
                    PaymentStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PaymentErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DropsReleased = table.Column<bool>(type: "boolean", nullable: false),
                    ChestSpotReserved = table.Column<bool>(type: "boolean", nullable: false),
                    ChestCreated = table.Column<bool>(type: "boolean", nullable: false),
                    ItemsResolved = table.Column<bool>(type: "boolean", nullable: false),
                    RescuePending = table.Column<bool>(type: "boolean", nullable: false),
                    RescueCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    PreviousGameMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RescueEndsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ChestX = table.Column<int>(type: "integer", nullable: true),
                    ChestY = table.Column<int>(type: "integer", nullable: true),
                    ChestZ = table.Column<int>(type: "integer", nullable: true),
                    ChestSecondX = table.Column<int>(type: "integer", nullable: true),
                    ChestSecondY = table.Column<int>(type: "integer", nullable: true),
                    ChestSecondZ = table.Column<int>(type: "integer", nullable: true),
                    FinalX = table.Column<double>(type: "double precision", nullable: true),
                    FinalY = table.Column<double>(type: "double precision", nullable: true),
                    FinalZ = table.Column<double>(type: "double precision", nullable: true),
                    BackendUnavailable = table.Column<bool>(type: "boolean", nullable: false),
                    CompensationPending = table.Column<bool>(type: "boolean", nullable: false),
                    Compensated = table.Column<bool>(type: "boolean", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinecraftDeathRecoveries", x => x.DeathId);
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

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftLinks_UserId_Confirmed_UnlinkedAtUtc",
                table: "MinecraftLinks",
                columns: new[] { "UserId", "Confirmed", "UnlinkedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftDeathRecoveries_PlayerUuid_OfferExpiresAtUtc",
                table: "MinecraftDeathRecoveries",
                columns: new[] { "PlayerUuid", "OfferExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftDeathRecoveries_PurchaseRequestId",
                table: "MinecraftDeathRecoveries",
                column: "PurchaseRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftDeathRecoveries_Stage_UpdatedAtUtc",
                table: "MinecraftDeathRecoveries",
                columns: new[] { "Stage", "UpdatedAtUtc" });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MinecraftDeathRecoveries");

            migrationBuilder.DropTable(
                name: "MinecraftLinkCodes");

            migrationBuilder.DropTable(
                name: "MinecraftRatingTransactions");

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

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "MinecraftLinks",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);
        }
    }
}
