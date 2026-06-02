using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Observability.Api.Migrations
{
    /// <inheritdoc />
    public partial class CompileCheck_20260601_233749 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Path",
                table: "PageViews",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);

            migrationBuilder.AddColumn<string>(
                name: "Action",
                table: "PageViews",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DurationMs",
                table: "PageViews",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Method",
                table: "PageViews",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusCode",
                table: "PageViews",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                table: "PageViews",
                type: "character varying(800)",
                maxLength: 800,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "PageViews",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PageViews_Path_CreatedAt",
                table: "PageViews",
                columns: new[] { "Path", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PageViews_UserId_CreatedAt",
                table: "PageViews",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PageViews_Path_CreatedAt",
                table: "PageViews");

            migrationBuilder.DropIndex(
                name: "IX_PageViews_UserId_CreatedAt",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "Action",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "Method",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "StatusCode",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                table: "PageViews");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "PageViews");

            migrationBuilder.AlterColumn<string>(
                name: "Path",
                table: "PageViews",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);
        }
    }
}
