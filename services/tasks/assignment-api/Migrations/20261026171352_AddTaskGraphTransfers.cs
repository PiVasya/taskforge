using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Tasks.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskGraphTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskGraphTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Lane = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    Phase = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Percent = table.Column<int>(type: "integer", nullable: false),
                    CompletedItems = table.Column<int>(type: "integer", nullable: false),
                    TotalItems = table.Column<int>(type: "integer", nullable: false),
                    BytesWritten = table.Column<long>(type: "bigint", nullable: false),
                    ResultBytes = table.Column<long>(type: "bigint", nullable: false),
                    CurrentItem = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorJson = table.Column<string>(type: "jsonb", nullable: true),
                    ResultStatus = table.Column<int>(type: "integer", nullable: true),
                    CancelRequested = table.Column<bool>(type: "boolean", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskGraphTransfers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskGraphTransferParts",
                columns: table => new
                {
                    TransferId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskGraphTransferParts", x => new { x.TransferId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_TaskGraphTransferParts_TaskGraphTransfers_TransferId",
                        column: x => x.TransferId,
                        principalTable: "TaskGraphTransfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskGraphTransfers_ExpiresAt",
                table: "TaskGraphTransfers",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskGraphTransfers_Lane_State_CreatedAt",
                table: "TaskGraphTransfers",
                columns: new[] { "Lane", "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskGraphTransfers_UserId_CourseId_CreatedAt",
                table: "TaskGraphTransfers",
                columns: new[] { "UserId", "CourseId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskGraphTransferParts");

            migrationBuilder.DropTable(
                name: "TaskGraphTransfers");
        }
    }
}
