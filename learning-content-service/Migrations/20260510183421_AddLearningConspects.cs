using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace learningcontentservice.Migrations
{
    /// <inheritdoc />
    public partial class AddLearningConspects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Conspects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Subtitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Lead = table.Column<string>(type: "text", nullable: true),
                    SubjectCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ExamCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    SectionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Kind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    EstimatedMinutes = table.Column<int>(type: "integer", nullable: false),
                    BadgesJson = table.Column<string>(type: "text", nullable: true),
                    ContentJson = table.Column<string>(type: "text", nullable: false),
                    SearchText = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conspects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConspectTaskLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConspectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    TaskType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceService = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TaskSlug = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: true),
                    TaskFilterJson = table.Column<string>(type: "text", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ButtonText = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    GroupTitle = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    AnchorBlockId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConspectTaskLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Courses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentCourseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Slug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ShortTitle = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SubjectCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ExamCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    SectionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Courses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CourseTaskLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceService = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TaskSlug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    GroupTitle = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseTaskLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Pages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Kind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    BodyMarkdown = table.Column<string>(type: "text", nullable: true),
                    BodyJson = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conspects_CourseId_Slug",
                table: "Conspects",
                columns: new[] { "CourseId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conspects_CourseId_SortOrder",
                table: "Conspects",
                columns: new[] { "CourseId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Conspects_SubjectCode_ExamCode_SectionCode",
                table: "Conspects",
                columns: new[] { "SubjectCode", "ExamCode", "SectionCode" });

            migrationBuilder.CreateIndex(
                name: "IX_ConspectTaskLinks_ConspectId_SortOrder",
                table: "ConspectTaskLinks",
                columns: new[] { "ConspectId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ConspectTaskLinks_TaskId",
                table: "ConspectTaskLinks",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_ConspectTaskLinks_TaskSlug",
                table: "ConspectTaskLinks",
                column: "TaskSlug");

            migrationBuilder.CreateIndex(
                name: "IX_Courses_ParentCourseId",
                table: "Courses",
                column: "ParentCourseId");

            migrationBuilder.CreateIndex(
                name: "IX_Courses_Slug",
                table: "Courses",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Courses_SubjectCode_ExamCode_SectionCode",
                table: "Courses",
                columns: new[] { "SubjectCode", "ExamCode", "SectionCode" });

            migrationBuilder.CreateIndex(
                name: "IX_CourseTaskLinks_CourseId_SortOrder",
                table: "CourseTaskLinks",
                columns: new[] { "CourseId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CourseTaskLinks_TaskId",
                table: "CourseTaskLinks",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_CourseId_Slug",
                table: "Pages",
                columns: new[] { "CourseId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pages_CourseId_SortOrder",
                table: "Pages",
                columns: new[] { "CourseId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Conspects");

            migrationBuilder.DropTable(
                name: "ConspectTaskLinks");

            migrationBuilder.DropTable(
                name: "Courses");

            migrationBuilder.DropTable(
                name: "CourseTaskLinks");

            migrationBuilder.DropTable(
                name: "Pages");
        }
    }
}
