using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace quiztaskservice.Migrations
{
    /// <inheritdoc />
    public partial class InitialMicroserviceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnswerJson = table.Column<string>(type: "text", nullable: false),
                    IsCorrect = table.Column<bool>(type: "boolean", nullable: false),
                    Score = table.Column<decimal>(type: "numeric", nullable: false),
                    MaxScore = table.Column<decimal>(type: "numeric", nullable: false),
                    TimeSpentSeconds = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Progress",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Solved = table.Column<bool>(type: "boolean", nullable: false),
                    BestScore = table.Column<decimal>(type: "numeric", nullable: false),
                    BestScorePercent = table.Column<decimal>(type: "numeric", nullable: false),
                    AttemptsCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirstSolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Progress", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    SubjectCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ExamCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SectionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Difficulty = table.Column<int>(type: "integer", nullable: false),
                    TagsJson = table.Column<string>(type: "text", nullable: true),
                    SourceName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    SourceYear = table.Column<int>(type: "integer", nullable: true),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    CorrectAnswerJson = table.Column<string>(type: "text", nullable: false),
                    ExplanationJson = table.Column<string>(type: "text", nullable: false),
                    ChangeComment = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_UserId_ClientAttemptId",
                table: "Attempts",
                columns: new[] { "UserId", "ClientAttemptId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_UserId_TaskId_CreatedAt",
                table: "Attempts",
                columns: new[] { "UserId", "TaskId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Progress_UserId_Solved",
                table: "Progress",
                columns: new[] { "UserId", "Solved" });

            migrationBuilder.CreateIndex(
                name: "IX_Progress_UserId_TaskId",
                table: "Progress",
                columns: new[] { "UserId", "TaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Slug",
                table: "Tasks",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_SubjectCode_ExamCode_SectionCode",
                table: "Tasks",
                columns: new[] { "SubjectCode", "ExamCode", "SectionCode" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskVersions_TaskId_VersionNumber",
                table: "TaskVersions",
                columns: new[] { "TaskId", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attempts");

            migrationBuilder.DropTable(
                name: "Progress");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "TaskVersions");
        }
    }
}
