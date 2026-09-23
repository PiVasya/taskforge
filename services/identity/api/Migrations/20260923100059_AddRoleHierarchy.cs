using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Identity.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAssignable",
                table: "FeatureRoles",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                table: "FeatureRoles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Rank",
                table: "FeatureRoles",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddCheckConstraint(
                name: "CK_FeatureRoles_Rank_NonNegative",
                table: "FeatureRoles",
                sql: "\"Rank\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FeatureRoles_Rank_NonNegative",
                table: "FeatureRoles");

            migrationBuilder.DropColumn(
                name: "IsAssignable",
                table: "FeatureRoles");

            migrationBuilder.DropColumn(
                name: "IsSystem",
                table: "FeatureRoles");

            migrationBuilder.DropColumn(
                name: "Rank",
                table: "FeatureRoles");
        }
    }
}
