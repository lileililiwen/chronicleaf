using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStaticBlogExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "static_blog_export_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_path = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    asset_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    selection_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    frozen_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_static_blog_export_requests", x => x.id);
                    table.CheckConstraint(
                        "ck_static_blog_export_requests_selection_hash_sha256_hex",
                        "selection_hash ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "fk_static_blog_export_requests_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_static_blog_export_requests_project_id",
                table: "static_blog_export_requests",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_static_blog_export_requests_scope_id_operation_id",
                table: "static_blog_export_requests",
                columns: new[] { "scope_id", "operation_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "static_blog_export_requests");
        }
    }
}
