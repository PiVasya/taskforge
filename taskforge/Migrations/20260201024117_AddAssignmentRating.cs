using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace taskforge.Migrations
{
    public partial class AddAssignmentRating : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'TaskAssignments'
          AND column_name = 'Rating'
    ) THEN
        ALTER TABLE ""TaskAssignments"" ADD COLUMN ""Rating"" integer NOT NULL DEFAULT 1;
    ELSE
        ALTER TABLE ""TaskAssignments"" ALTER COLUMN ""Rating"" SET DEFAULT 1;
    END IF;
END $$;
");

            // Если ранее колонка была создана с DEFAULT 0 — подтягиваем к логике "не задано => 1"
            migrationBuilder.Sql("UPDATE \"TaskAssignments\" SET \"Rating\" = 1 WHERE \"Rating\" = 0;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"TaskAssignments\" DROP COLUMN IF EXISTS \"Rating\";");
        }
    }
}
