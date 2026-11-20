using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Solutions.Api.Migrations;

public partial class AdminSolutionFeedOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AdminSolutionFeedEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                Score = table.Column<int>(type: "integer", nullable: true),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AdminSolutionFeedEvents", x => x.Id));
        migrationBuilder.CreateIndex(
            name: "IX_AdminSolutionFeedEvents_OccurredAtUtc_Id",
            table: "AdminSolutionFeedEvents",
            columns: new[] { "OccurredAtUtc", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_AdminSolutionFeedEvents_Kind_ItemId_OccurredAtUtc",
            table: "AdminSolutionFeedEvents",
            columns: new[] { "Kind", "ItemId", "OccurredAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "AdminSolutionFeedEvents");
}
