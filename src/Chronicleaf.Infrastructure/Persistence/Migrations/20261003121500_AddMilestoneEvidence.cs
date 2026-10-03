using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMilestoneEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "evidence_bundles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_bundles", x => x.id);
                    table.ForeignKey(
                        name: "fk_evidence_bundles_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "evidence_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bundle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_revision = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: true),
                    redactions = table.Column<string[]>(type: "text[]", nullable: false),
                    availability = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    supersedes_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_evidence_items_bundle_id",
                        column: x => x.bundle_id,
                        principalTable: "evidence_bundles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_evidence_items_media_id",
                        column: x => x.media_id,
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_evidence_items_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_evidence_items_supersedes_id",
                        column: x => x.supersedes_id,
                        principalTable: "evidence_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_bundles_milestone_revision_id",
                table: "evidence_bundles",
                columns: new[] { "milestone_id", "revision", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_evidence_bundles_milestone_id_revision",
                table: "evidence_bundles",
                columns: new[] { "milestone_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_evidence_items_bundle_id_id",
                table: "evidence_items",
                columns: new[] { "bundle_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_items_media_id",
                table: "evidence_items",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_items_milestone_id",
                table: "evidence_items",
                column: "milestone_id");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_items_supersedes_id",
                table: "evidence_items",
                column: "supersedes_id");

            // The honesty constraints. These are part of the evidence argument rather than bookkeeping:
            // a row that says a source was unavailable cannot also carry a payload or point at media,
            // and a row that says a source was partial has to say what was left out. Neither rule is
            // left to a caller remembering to apply it, and neither can be satisfied by a future
            // writer that has not read the service.
            migrationBuilder.AddCheckConstraint(
                name: "ck_evidence_bundles_revision_positive",
                table: "evidence_bundles",
                sql: "revision >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_evidence_items_availability",
                table: "evidence_items",
                sql: "availability IN ('complete', 'partial', 'unavailable')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_evidence_items_kind",
                table: "evidence_items",
                sql: "kind IN ('project-metadata', 'commit', 'diff', 'release', 'issue', 'pr', 'screenshot', 'manual', 'limitations', 'next-steps')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_evidence_items_payload_sha256",
                table: "evidence_items",
                sql: "payload_sha256 ~ '^[0-9a-f]{64}$'");

            // A gap has to say why. An unavailable row with no text would be exactly the empty
            // false-success source this package exists to prevent, so the database refuses it.
            migrationBuilder.AddCheckConstraint(
                name: "ck_evidence_items_unavailable_has_a_reason",
                table: "evidence_items",
                sql: "availability <> 'unavailable' OR btrim(text) <> ''");

            // An unavailable source produced nothing, so it has no media to point at.
            migrationBuilder.AddCheckConstraint(
                name: "ck_evidence_items_unavailable_has_no_media",
                table: "evidence_items",
                sql: "availability <> 'unavailable' OR media_id IS NULL");

            // A partial source that names no exclusion is indistinguishable from a complete one.
            migrationBuilder.AddCheckConstraint(
                name: "ck_evidence_items_partial_names_an_exclusion",
                table: "evidence_items",
                sql: "availability <> 'partial' OR cardinality(redactions) > 0");

            // Collected evidence has to carry what was collected.
            migrationBuilder.AddCheckConstraint(
                name: "ck_evidence_items_collected_has_text",
                table: "evidence_items",
                sql: "availability = 'unavailable' OR text <> ''");

            AppendOnlyTriggers(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_evidence_items_append_only ON evidence_items;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_evidence_bundles_append_only ON evidence_bundles;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS chronicleaf_refuse_evidence_mutation();");

            migrationBuilder.DropTable(
                name: "evidence_items");

            migrationBuilder.DropTable(
                name: "evidence_bundles");
        }

        /// <summary>
        /// Installs the two triggers that make both evidence tables append-only, and the one function
        /// they share.
        /// </summary>
        /// <remarks>
        /// Append-only is a property of the schema here, not a convention the store follows. The
        /// service already issues no UPDATE and no DELETE against either table, and this closes the
        /// gap that a service rule cannot: any other writer - a future package, a migration, a
        /// psql session, a bug - is refused the same way. A bundle that is corrected is replaced by a
        /// new bundle, and an item that is superseded keeps its row.
        /// <para>
        /// The function raises a normal exception, so the statement fails and its transaction rolls
        /// back. It reports the table and the statement it refused and nothing about the row's
        /// contents, so the message cannot leak collected evidence into a log.
        /// </para>
        /// </remarks>
        private static void AppendOnlyTriggers(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION chronicleaf_refuse_evidence_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $fn$
                BEGIN
                    RAISE EXCEPTION
                        'evidence is append-only: % on % is refused (MILESTONE-EVIDENCE-001)',
                        TG_OP, TG_TABLE_NAME
                        USING ERRCODE = 'restrict_violation';
                END;
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_evidence_bundles_append_only
                BEFORE UPDATE OR DELETE ON evidence_bundles
                FOR EACH ROW
                EXECUTE FUNCTION chronicleaf_refuse_evidence_mutation();
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_evidence_items_append_only
                BEFORE UPDATE OR DELETE ON evidence_items
                FOR EACH ROW
                EXECUTE FUNCTION chronicleaf_refuse_evidence_mutation();
                """);
        }
    }
}
