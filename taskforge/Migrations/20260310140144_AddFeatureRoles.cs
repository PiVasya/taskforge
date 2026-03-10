using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatureRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.CreateTable(
                name: "FeatureRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserFeatureRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserFeatureRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserFeatureRoles_FeatureRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "FeatureRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserFeatureRoles_Users_AssignedByUserId",
                        column: x => x.AssignedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UserFeatureRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureRoles_Code",
                table: "FeatureRoles",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserFeatureRoles_AssignedByUserId",
                table: "UserFeatureRoles",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserFeatureRoles_RoleId",
                table: "UserFeatureRoles",
                column: "RoleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserFeatureRoles");

            migrationBuilder.DropTable(
                name: "FeatureRoles");
        }
    }
}
