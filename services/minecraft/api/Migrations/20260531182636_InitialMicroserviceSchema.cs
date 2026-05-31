using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Minecraft.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialMicroserviceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MinecraftChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Author = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinecraftChatMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MinecraftLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlayerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    PlayerUuid = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinecraftLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceSchemaMarkers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceSchemaMarkers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftChatMessages_CreatedAt",
                table: "MinecraftChatMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MinecraftLinks_Code",
                table: "MinecraftLinks",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MinecraftChatMessages");

            migrationBuilder.DropTable(
                name: "MinecraftLinks");

            migrationBuilder.DropTable(
                name: "ServiceSchemaMarkers");
        }
    }
}
