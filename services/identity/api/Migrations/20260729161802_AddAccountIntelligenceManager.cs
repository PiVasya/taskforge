using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Identity.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountIntelligenceManager : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceHash",
                table: "UserLoginLogs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserLoginLogs_DeviceHash_LoginAt",
                table: "UserLoginLogs",
                columns: new[] { "DeviceHash", "LoginAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserLoginLogs_LoginAt",
                table: "UserLoginLogs",
                column: "LoginAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserLoginLogs_DeviceHash_LoginAt",
                table: "UserLoginLogs");

            migrationBuilder.DropIndex(
                name: "IX_UserLoginLogs_LoginAt",
                table: "UserLoginLogs");

            migrationBuilder.DropColumn(
                name: "DeviceHash",
                table: "UserLoginLogs");
        }
    }
}
