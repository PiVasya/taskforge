using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    /// <inheritdoc />
    public partial class AddUserQuotaBucketsAndUserImageTaskSolutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserImageTaskSolutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskAssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsTrial = table.Column<bool>(type: "boolean", nullable: false),
                    SubmittedCode = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReferenceKey = table.Column<string>(type: "text", nullable: true),
                    SubmittedKey = table.Column<string>(type: "text", nullable: true),
                    SimilarityPercent = table.Column<double>(type: "double precision", nullable: true),
                    ThresholdPercent = table.Column<double>(type: "double precision", nullable: true),
                    Passed = table.Column<bool>(type: "boolean", nullable: true),
                    Stdout = table.Column<string>(type: "text", nullable: false),
                    Stderr = table.Column<string>(type: "text", nullable: false),
                    RunnerError = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserImageTaskSolutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserImageTaskSolutions_TaskAssignments_TaskAssignmentId",
                        column: x => x.TaskAssignmentId,
                        principalTable: "TaskAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserImageTaskSolutions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserQuotaBuckets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BucketType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Tokens = table.Column<int>(type: "integer", nullable: false),
                    LastRefillAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserQuotaBuckets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserImageTaskSolutions_TaskAssignmentId",
                table: "UserImageTaskSolutions",
                column: "TaskAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_UserImageTaskSolutions_UserId_TaskAssignmentId_CreatedAtUtc",
                table: "UserImageTaskSolutions",
                columns: new[] { "UserId", "TaskAssignmentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UserQuotaBuckets_UserId_BucketType",
                table: "UserQuotaBuckets",
                columns: new[] { "UserId", "BucketType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserImageTaskSolutions");

            migrationBuilder.DropTable(
                name: "UserQuotaBuckets");
        }
    }
}
