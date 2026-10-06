using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicProjectJourney : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_journeys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    selection_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_journeys", x => x.id);
                    table.CheckConstraint(
                        "ck_project_journeys_slug_kebab_case",
                        "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint(
                        "ck_project_journeys_selection_hash_sha256_hex",
                        "selection_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint(
                        "ck_project_journeys_snapshot_ref_sha256_hex",
                        "snapshot_ref ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint(
                        "ck_project_journeys_revision_nonnegative",
                        "revision >= 0");
                    table.ForeignKey(
                        name: "fk_project_journeys_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_journeys_project_id",
                table: "project_journeys",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_project_journeys_scope_id_project_id",
                table: "project_journeys",
                columns: new[] { "scope_id", "project_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_project_journeys_slug",
                table: "project_journeys",
                column: "slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_journeys");
        }
    }
}
