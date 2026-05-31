using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TelegramQuizBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialMicroserviceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "authorized_teachers",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authorized_teachers", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    order_index = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "category_stats",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    subcategory = table.Column<string>(type: "text", nullable: false),
                    correct = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    incorrect = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_category_stats", x => new { x.user_id, x.category, x.subcategory });
                });

            migrationBuilder.CreateTable(
                name: "diagnostic_results",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    subcategory = table.Column<string>(type: "text", nullable: false),
                    correct = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_diagnostic_results", x => new { x.user_id, x.subcategory });
                });

            migrationBuilder.CreateTable(
                name: "diagnostic_tests",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    question_ids = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_diagnostic_tests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "progress",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    total = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    correct = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    incorrect = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    experience = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_progress", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "quizzes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    question = table.Column<string>(type: "text", nullable: false),
                    options = table.Column<string>(type: "text", nullable: true),
                    correct_option_id = table.Column<int>(type: "integer", nullable: true),
                    explanation = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<string>(type: "text", nullable: false, defaultValue: "Остальное"),
                    subcategory = table.Column<string>(type: "text", nullable: false, defaultValue: "Без подкатегории"),
                    type = table.Column<string>(type: "text", nullable: false, defaultValue: "quiz"),
                    answer = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    image = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quizzes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "smart_progress",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    subcategory = table.Column<string>(type: "text", nullable: false),
                    correct = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    incorrect = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_smart_progress", x => new { x.user_id, x.subcategory });
                });

            migrationBuilder.CreateTable(
                name: "start_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    username = table.Column<string>(type: "text", nullable: true),
                    full_name = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_start_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "student_contacts",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    first_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    last_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    full_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    language_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_message_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "unknown"),
                    last_message_text = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    message_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    is_hidden = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    hidden_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    hidden_by_teacher_id = table.Column<long>(type: "bigint", nullable: true),
                    note = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    search_text = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_contacts", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "subcategory_stats",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    subcategory = table.Column<string>(type: "text", nullable: false),
                    correct = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    incorrect = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subcategory_stats", x => new { x.user_id, x.category, x.subcategory });
                });

            migrationBuilder.CreateTable(
                name: "technical_break",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    end_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technical_break", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "test_questions",
                columns: table => new
                {
                    test_id = table.Column<long>(type: "bigint", nullable: false),
                    quiz_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_questions", x => new { x.test_id, x.quiz_id });
                });

            migrationBuilder.CreateTable(
                name: "user_answers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    username = table.Column<string>(type: "text", nullable: true),
                    quiz_id = table.Column<long>(type: "bigint", nullable: false),
                    correct = table.Column<bool>(type: "boolean", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_answers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_settings",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    learning_mode = table.Column<string>(type: "text", nullable: false, defaultValue: "normal"),
                    selected_category = table.Column<string>(type: "text", nullable: false, defaultValue: "all"),
                    diagnostic_completed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_settings", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "whitelist",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    expire_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_notification = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_whitelist", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_quizzes_category_subcategory",
                table: "quizzes",
                columns: new[] { "category", "subcategory" });

            migrationBuilder.CreateIndex(
                name: "IX_student_contacts_is_hidden",
                table: "student_contacts",
                column: "is_hidden");

            migrationBuilder.CreateIndex(
                name: "IX_student_contacts_last_seen_at",
                table: "student_contacts",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "IX_student_contacts_search_text",
                table: "student_contacts",
                column: "search_text");

            migrationBuilder.CreateIndex(
                name: "IX_student_contacts_username",
                table: "student_contacts",
                column: "username");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authorized_teachers");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "category_stats");

            migrationBuilder.DropTable(
                name: "diagnostic_results");

            migrationBuilder.DropTable(
                name: "diagnostic_tests");

            migrationBuilder.DropTable(
                name: "progress");

            migrationBuilder.DropTable(
                name: "quizzes");

            migrationBuilder.DropTable(
                name: "smart_progress");

            migrationBuilder.DropTable(
                name: "start_log");

            migrationBuilder.DropTable(
                name: "student_contacts");

            migrationBuilder.DropTable(
                name: "subcategory_stats");

            migrationBuilder.DropTable(
                name: "technical_break");

            migrationBuilder.DropTable(
                name: "test_questions");

            migrationBuilder.DropTable(
                name: "user_answers");

            migrationBuilder.DropTable(
                name: "user_settings");

            migrationBuilder.DropTable(
                name: "whitelist");
        }
    }
}
