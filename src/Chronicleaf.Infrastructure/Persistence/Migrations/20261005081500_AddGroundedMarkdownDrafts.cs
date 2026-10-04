using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroundedMarkdownDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "content_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    current_revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "draft"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_assets_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "content_draft_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    preview_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consent_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evidence_revision = table.Column<long>(type: "bigint", nullable: false),
                    voice_revision = table.Column<long>(type: "bigint", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_draft_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_draft_requests_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_content_draft_requests_preview_id",
                        column: x => x.preview_id,
                        principalTable: "generation_previews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "content_revisions",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evidence_bundle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evidence_revision = table.Column<long>(type: "bigint", nullable: false),
                    voice_revision = table.Column<long>(type: "bigint", nullable: false),
                    voice_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    summary = table.Column<string>(type: "jsonb", nullable: false),
                    body_markdown = table.Column<string>(type: "text", nullable: false),
                    body_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    schema_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_revisions", x => new { x.asset_id, x.revision });
                    table.ForeignKey(
                        name: "fk_content_revisions_asset_id",
                        column: x => x.asset_id,
                        principalTable: "content_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_content_revisions_evidence_bundle_id",
                        column: x => x.evidence_bundle_id,
                        principalTable: "evidence_bundles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_content_assets_milestone_id",
                table: "content_assets",
                column: "milestone_id");

            migrationBuilder.CreateIndex(
                name: "ux_content_assets_scope_id_milestone_id_type",
                table: "content_assets",
                columns: new[] { "scope_id", "milestone_id", "type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_content_draft_requests_milestone_id",
                table: "content_draft_requests",
                column: "milestone_id");

            migrationBuilder.CreateIndex(
                name: "IX_content_draft_requests_preview_id",
                table: "content_draft_requests",
                column: "preview_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_draft_requests_scope_id_milestone_id_created_at",
                table: "content_draft_requests",
                columns: new[] { "scope_id", "milestone_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_content_draft_requests_scope_id_operation_id",
                table: "content_draft_requests",
                columns: new[] { "scope_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_revisions_asset_id_revision_id",
                table: "content_revisions",
                columns: new[] { "asset_id", "revision", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_content_revisions_evidence_bundle_id",
                table: "content_revisions",
                column: "evidence_bundle_id");

            migrationBuilder.CreateIndex(
                name: "ux_content_revisions_id",
                table: "content_revisions",
                column: "id",
                unique: true);

            // Four constraints, all of them claims the design makes about a draft and none of them
            // enforced only by application code. They are the storage half of the grounding argument:
            // a row that could hold a version number below one, a head that could be negative, a draft
            // claiming to be grounded in evidence revision zero, or a digest that is not a digest would
            // each be a row the read path cannot report honestly.
            migrationBuilder.AddCheckConstraint(
                name: "ck_content_assets_current_revision_non_negative",
                table: "content_assets",
                sql: "current_revision >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_content_revisions_revision_at_least_one",
                table: "content_revisions",
                sql: "revision >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_content_revisions_evidence_revision_at_least_one",
                table: "content_revisions",
                sql: "evidence_revision >= 1");

            // Lowercase hex only, on purpose. An uppercase digest is refused rather than normalised, so a
            // body hash can never be recorded in two spellings for one body and then fail to match
            // itself - which is the same rule the evidence package applies to a payload digest.
            migrationBuilder.AddCheckConstraint(
                name: "ck_content_revisions_body_hash_sha256_hex",
                table: "content_revisions",
                sql: "body_hash ~ '^[0-9a-f]{64}$'");

            // The trigger is the requirement, not a nicety. A revision's body_hash is what the review
            // package will bind an approval to, so a body that could be edited after the fact would make
            // that binding a signature over nothing. Refusing UPDATE and DELETE from any writer is the
            // same append-only contract the evidence tables already have, and it is why this package can
            // promise that prior revisions are preserved rather than merely that it does not edit them.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION chronicleaf_refuse_content_revision_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION
                        'content_revisions is append-only: a stored draft revision is never rewritten or removed (attempted %).',
                        TG_OP
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER trg_content_revisions_append_only
                BEFORE UPDATE OR DELETE ON content_revisions
                FOR EACH ROW
                EXECUTE FUNCTION chronicleaf_refuse_content_revision_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_content_revisions_append_only ON content_revisions;
                DROP FUNCTION IF EXISTS chronicleaf_refuse_content_revision_mutation();
                """);

            migrationBuilder.DropTable(
                name: "content_draft_requests");

            migrationBuilder.DropTable(
                name: "content_revisions");

            migrationBuilder.DropTable(
                name: "content_assets");
        }
    }
}
