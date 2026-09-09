using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Solutions.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSqlDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionTarget",
                table: "SolutionSubmissions",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SqlEngineProfileId",
                table: "SolutionSubmissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SqlSpecVersionId",
                table: "SolutionSubmissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SolutionSubmissions_SqlSpecVersionId_SqlEngineProfileId",
                table: "SolutionSubmissions",
                columns: new[] { "SqlSpecVersionId", "SqlEngineProfileId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SolutionSubmissions_SqlBinding",
                table: "SolutionSubmissions",
                sql: "(\"SqlSpecVersionId\" IS NULL AND \"SqlEngineProfileId\" IS NULL) OR (\"SqlSpecVersionId\" IS NOT NULL AND \"SqlEngineProfileId\" IS NOT NULL AND \"ExecutionTarget\" IS NOT NULL AND length(\"ExecutionTarget\") > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SolutionSubmissions_SqlSpecVersionId_SqlEngineProfileId",
                table: "SolutionSubmissions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SolutionSubmissions_SqlBinding",
                table: "SolutionSubmissions");

            migrationBuilder.DropColumn(
                name: "ExecutionTarget",
                table: "SolutionSubmissions");

            migrationBuilder.DropColumn(
                name: "SqlEngineProfileId",
                table: "SolutionSubmissions");

            migrationBuilder.DropColumn(
                name: "SqlSpecVersionId",
                table: "SolutionSubmissions");
        }
    }
}
