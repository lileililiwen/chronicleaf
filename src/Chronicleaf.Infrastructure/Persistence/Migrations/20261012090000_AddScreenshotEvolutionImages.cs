using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScreenshotEvolutionImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "evolution_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layout = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    manifest_json = table.Column<string>(type: "jsonb", nullable: false),
                    selection_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    artifact_media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evolution_images", x => x.id);
                    table.CheckConstraint("ck_evolution_images_layout", "layout IN ('before-after', 'horizontal-timeline', 'vertical-long', 'gallery')");
                    table.CheckConstraint("ck_evolution_images_status", "status IN ('draft', 'approved')");
                    table.CheckConstraint("ck_evolution_images_selection_hash_sha256_hex", "selection_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_evolution_images_artifact_sha256_hex", "artifact_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_evolution_images_dimensions_positive", "width > 0 AND height > 0");
                    table.ForeignKey(
                        name: "fk_evolution_images_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_evolution_images_artifact_media_id",
                        column: x => x.artifact_media_id,
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_evolution_images_artifact_media_id",
                table: "evolution_images",
                column: "artifact_media_id");

            migrationBuilder.CreateIndex(
                name: "IX_evolution_images_project_id",
                table: "evolution_images",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_evolution_images_scope_id_project_id_idempotency_key",
                table: "evolution_images",
                columns: new[] { "scope_id", "project_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evolution_images");
        }
    }
}
