using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEvolutionGifExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "evolution_gifs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_json = table.Column<string>(type: "jsonb", nullable: false),
                    selection_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    artifact_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    artifact_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    frame_count = table.Column<int>(type: "integer", nullable: false),
                    total_duration_ms = table.Column<int>(type: "integer", nullable: false),
                    loop = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evolution_gifs", x => x.id);
                    table.CheckConstraint("ck_evolution_gifs_status", "status IN ('draft', 'approved')");
                    table.CheckConstraint("ck_evolution_gifs_selection_hash_sha256_hex", "selection_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_evolution_gifs_artifact_sha256_hex", "artifact_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_evolution_gifs_dimensions_positive", "width > 0 AND height > 0");
                    table.CheckConstraint("ck_evolution_gifs_frame_count", "frame_count BETWEEN 2 AND 12");
                    table.CheckConstraint("ck_evolution_gifs_total_duration_ms", "total_duration_ms > 0 AND total_duration_ms <= 60000");
                    table.ForeignKey(
                        name: "fk_evolution_gifs_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_evolution_gifs_project_id",
                table: "evolution_gifs",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_evolution_gifs_scope_id_project_id_idempotency_key",
                table: "evolution_gifs",
                columns: new[] { "scope_id", "project_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evolution_gifs");
        }
    }
}
