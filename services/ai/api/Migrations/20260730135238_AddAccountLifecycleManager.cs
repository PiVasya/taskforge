using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Ai.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountLifecycleManager : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountManagementOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Phase = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    SourceUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OptionsJson = table.Column<string>(type: "jsonb", nullable: true),
                    SourceSnapshotJson = table.Column<string>(type: "jsonb", nullable: true),
                    TargetSnapshotJson = table.Column<string>(type: "jsonb", nullable: true),
                    StepsJson = table.Column<string>(type: "jsonb", nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorJson = table.Column<string>(type: "jsonb", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CancelRequested = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountManagementOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountManagementOperations_SourceUserId_CreatedAtUtc",
                table: "AccountManagementOperations",
                columns: new[] { "SourceUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountManagementOperations_Status_CreatedAtUtc",
                table: "AccountManagementOperations",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountManagementOperations_TargetUserId_CreatedAtUtc",
                table: "AccountManagementOperations",
                columns: new[] { "TargetUserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountManagementOperations");
        }
    }
}
