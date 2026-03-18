using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class AddUserActionLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserActionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActionType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Target = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", maxLength: 2048, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IsAuthenticated = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserActionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserActionLogs_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserActionLogs_Category_CreatedAtUtc",
                table: "UserActionLogs",
                columns: new[] { "Category", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UserActionLogs_CreatedAtUtc",
                table: "UserActionLogs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UserActionLogs_UserId_CreatedAtUtc",
                table: "UserActionLogs",
                columns: new[] { "UserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserActionLogs");
        }
    }
}
