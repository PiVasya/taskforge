using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TelegramQuizBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "student_contacts");
        }
    }
}
