using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScreenshotCuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_screenshot_candidates_milestone_id",
                table: "screenshot_candidates");

            migrationBuilder.AlterColumn<string>(
                name: "caption",
                table: "screenshot_candidates",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldDefaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "display_order",
                table: "screenshot_candidates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ux_screenshot_candidates_milestone_primary",
                table: "screenshot_candidates",
                column: "milestone_id",
                unique: true,
                filter: "is_primary");

            migrationBuilder.AddCheckConstraint(
                name: "ck_screenshot_candidates_status",
                table: "screenshot_candidates",
                sql: "status IN ('candidate', 'selected', 'rejected')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_screenshot_candidates_milestone_primary",
                table: "screenshot_candidates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_screenshot_candidates_status",
                table: "screenshot_candidates");

            migrationBuilder.DropColumn(
                name: "display_order",
                table: "screenshot_candidates");

            migrationBuilder.AlterColumn<string>(
                name: "caption",
                table: "screenshot_candidates",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldDefaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_screenshot_candidates_milestone_id",
                table: "screenshot_candidates",
                column: "milestone_id");
        }
    }
}
