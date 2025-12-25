using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using taskforge.Data;

namespace taskforge.Migrations
{
    // NOTE:
    // В репозитории уже есть миграции TestsTasks/TestsTasks2, но они пустые (Up/Down не создают таблицы),
    // из-за чего в БД нет таблиц для тестов и любые запросы в AssignmentService падают с
    // "relation \"UserTaskTestAttempts\" does not exist".
    // Эта миграция добавляет недостающие таблицы.
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20251226090000_CreateTaskTestTables")]
    public partial class CreateTaskTestTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskTestSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskAssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    PassPercent = table.Column<int>(type: "integer", nullable: false),
                    ShuffleQuestions = table.Column<bool>(type: "boolean", nullable: false),
                    ShuffleAnswers = table.Column<bool>(type: "boolean", nullable: false),
                    AttemptTimeLimitsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTestSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskTestSettings_TaskAssignments_TaskAssignmentId",
                        column: x => x.TaskAssignmentId,
                        principalTable: "TaskAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskTestQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskAssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Prompt = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DataJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTestQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskTestQuestions_TaskAssignments_TaskAssignmentId",
                        column: x => x.TaskAssignmentId,
                        principalTable: "TaskAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserTaskTestAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskAssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TimeLimitSeconds = table.Column<int>(type: "integer", nullable: true),
                    ScorePercent = table.Column<int>(type: "integer", nullable: true),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    TimeExpired = table.Column<bool>(type: "boolean", nullable: false),
                    QuestionOrderJson = table.Column<string>(type: "jsonb", nullable: false),
                    AnswersJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTaskTestAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserTaskTestAttempts_TaskAssignments_TaskAssignmentId",
                        column: x => x.TaskAssignmentId,
                        principalTable: "TaskAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserTaskTestAttempts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskTestSettings_TaskAssignmentId",
                table: "TaskTestSettings",
                column: "TaskAssignmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskTestQuestions_TaskAssignmentId_Order",
                table: "TaskTestQuestions",
                columns: new[] { "TaskAssignmentId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_UserTaskTestAttempts_TaskAssignmentId_UserId",
                table: "UserTaskTestAttempts",
                columns: new[] { "TaskAssignmentId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserTaskTestAttempts_UserId",
                table: "UserTaskTestAttempts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserTaskTestAttempts_TaskAssignmentId_UserId_AttemptNumber",
                table: "UserTaskTestAttempts",
                columns: new[] { "TaskAssignmentId", "UserId", "AttemptNumber" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserTaskTestAttempts");

            migrationBuilder.DropTable(
                name: "TaskTestQuestions");

            migrationBuilder.DropTable(
                name: "TaskTestSettings");
        }
    }
}
