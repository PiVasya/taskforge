using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Observability.Api.Migrations
{
    /// <inheritdoc />
    public partial class ObservabilityAnalyticsNetworkMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientCountry",
                table: "PageViews",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientIpHash",
                table: "PageViews",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientIpPrefix",
                table: "PageViews",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCode",
                table: "PageViews",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "PageViews",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "PageViews",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceId",
                table: "PageViews",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PageViews_Action_CreatedAt",
                table: "PageViews",
                columns: new[] { "Action", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PageViews_ClientIpHash_CreatedAt",
                table: "PageViews",
                columns: new[] { "ClientIpHash", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PageViews_StatusCode_CreatedAt",
                table: "PageViews",
                columns: new[] { "StatusCode", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PageViews_Action_CreatedAt",
                table: "PageViews");

            migrationBuilder.DropIndex(
                name: "IX_PageViews_ClientIpHash_CreatedAt",
                table: "PageViews");

            migrationBuilder.DropIndex(
                name: "IX_PageViews_StatusCode_CreatedAt",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "ClientCountry",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "ClientIpHash",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "ClientIpPrefix",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "ErrorCode",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "TraceId",
                table: "PageViews");
        }
    }
}
