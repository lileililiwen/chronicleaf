using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNarrativeMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "narrative_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    source_evidence_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    visibility = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_narrative_entries", x => x.id);
                    table.CheckConstraint("ck_narrative_entries_kind", "kind IN ('origin', 'audience', 'technical-route', 'decision', 'problem', 'feedback')");
                    table.CheckConstraint("ck_narrative_entries_revision_at_least_one", "revision >= 1");
                    table.CheckConstraint("ck_narrative_entries_visibility", "visibility IN ('private', 'exportable')");
                    table.ForeignKey(
                        name: "fk_narrative_entries_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "narrative_refresh_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cutoff = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    selected_entry_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_narrative_refresh_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_narrative_refresh_requests_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "project_summaries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    through_milestone_id = table.Column<Guid>(type: "uuid", nullable: true),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    body_json = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    stale = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_summaries", x => x.id);
                    table.CheckConstraint("ck_project_summaries_input_hash_sha256_hex", "input_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_project_summaries_revision_at_least_one", "revision >= 1");
                    table.ForeignKey(
                        name: "fk_project_summaries_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_narrative_entries_project_id_occurred_at_id",
                table: "narrative_entries",
                columns: new[] { "project_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_narrative_refresh_requests_project_id",
                table: "narrative_refresh_requests",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_narrative_refresh_requests_scope_id_operation_id",
                table: "narrative_refresh_requests",
                columns: new[] { "scope_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_project_summaries_project_id",
                table: "project_summaries",
                column: "project_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "narrative_entries");

            migrationBuilder.DropTable(
                name: "narrative_refresh_requests");

            migrationBuilder.DropTable(
                name: "project_summaries");
        }
    }
}
