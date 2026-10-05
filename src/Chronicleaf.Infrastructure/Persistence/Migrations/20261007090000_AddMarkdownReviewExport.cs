using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarkdownReviewExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "content_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision = table.Column<long>(type: "bigint", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    body_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evidence_revision = table.Column<long>(type: "bigint", nullable: false),
                    voice_snapshot_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    media_selection_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    artifact_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    invalidated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_approvals", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_approvals_asset_id",
                        column: x => x.asset_id,
                        principalTable: "content_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_content_approvals_asset_id",
                table: "content_approvals",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ux_content_approvals_scope_id_asset_id_revision",
                table: "content_approvals",
                columns: new[] { "scope_id", "asset_id", "content_revision" },
                unique: true);

            // Four constraints, all of them claims the design makes about an approval and none of
            // them enforced only by application code. A row that could name revision zero, bind
            // evidence revision zero, or carry a digest that is not a digest would each be a row
            // the export could not honestly re-check - and the export re-checks every one of them
            // against the stored state before serving a byte.
            migrationBuilder.AddCheckConstraint(
                name: "ck_content_approvals_content_revision_at_least_one",
                table: "content_approvals",
                sql: "content_revision >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_content_approvals_evidence_revision_at_least_one",
                table: "content_approvals",
                sql: "evidence_revision >= 1");

            // Lowercase hex only, on purpose: the same rule the drafts package applies to a body
            // hash, so an approval can never record a digest in two spellings for one payload.
            migrationBuilder.AddCheckConstraint(
                name: "ck_content_approvals_body_hash_sha256_hex",
                table: "content_approvals",
                sql: "body_hash ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_content_approvals_voice_snapshot_hash_sha256_hex",
                table: "content_approvals",
                sql: "voice_snapshot_hash ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_content_approvals_media_selection_hash_sha256_hex",
                table: "content_approvals",
                sql: "media_selection_hash ~ '^[0-9a-f]{64}$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_approvals");
        }
    }
}
