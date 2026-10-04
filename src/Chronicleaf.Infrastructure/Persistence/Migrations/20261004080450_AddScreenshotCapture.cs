using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScreenshotCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "capture_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    urls = table.Column<string>(type: "jsonb", nullable: false),
                    viewports = table.Column<string>(type: "jsonb", nullable: false),
                    wait_selector = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    wait_milliseconds = table.Column<int>(type: "integer", nullable: false),
                    full_page = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    expected_bundle_revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capture_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_capture_requests_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "screenshot_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    final_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    viewport = table.Column<string>(type: "jsonb", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "candidate"),
                    caption = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, defaultValue: ""),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_screenshot_candidates", x => x.id);
                    table.ForeignKey(
                        name: "fk_screenshot_candidates_media_id",
                        column: x => x.media_id,
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_screenshot_candidates_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_capture_requests_milestone_id",
                table: "capture_requests",
                column: "milestone_id");

            migrationBuilder.CreateIndex(
                name: "ux_capture_requests_scope_id_operation_id",
                table: "capture_requests",
                columns: new[] { "scope_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_screenshot_candidates_media_id",
                table: "screenshot_candidates",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "IX_screenshot_candidates_milestone_id",
                table: "screenshot_candidates",
                column: "milestone_id");

            migrationBuilder.CreateIndex(
                name: "ix_screenshot_candidates_scope_id_milestone_id_created_at",
                table: "screenshot_candidates",
                columns: new[] { "scope_id", "milestone_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_screenshot_candidates_operation_id_source_url_viewport",
                table: "screenshot_candidates",
                columns: new[] { "operation_id", "source_url", "viewport" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "capture_requests");

            migrationBuilder.DropTable(
                name: "screenshot_candidates");
        }
    }
}
