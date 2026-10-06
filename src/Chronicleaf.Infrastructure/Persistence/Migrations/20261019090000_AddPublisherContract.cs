using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublisherContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "publication_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision = table.Column<long>(type: "bigint", nullable: false),
                    body_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evidence_revision = table.Column<long>(type: "bigint", nullable: false),
                    preview_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    remote_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    remote_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publication_attempts", x => x.id);
                    table.CheckConstraint("ck_publication_attempts_evidence_revision_positive", "evidence_revision >= 1");
                    table.CheckConstraint("ck_publication_attempts_mode_known", "mode IN ('draft', 'publish')");
                    table.CheckConstraint("ck_publication_attempts_revision_positive", "content_revision >= 1");
                    table.CheckConstraint("ck_publication_attempts_state_known", "state IN ('prepared', 'sending', 'published', 'remote-draft', 'unknown', 'failed')");
                    table.ForeignKey(
                        name: "fk_publication_attempts_asset_id",
                        column: x => x.asset_id,
                        principalTable: "content_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_publication_attempts_asset_id",
                table: "publication_attempts",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_publication_attempts_scope_id_asset_id",
                table: "publication_attempts",
                columns: new[] { "scope_id", "asset_id" });

            migrationBuilder.CreateIndex(
                name: "ux_publication_attempts_operation_id",
                table: "publication_attempts",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_publication_attempts_scope_id_idempotency_key",
                table: "publication_attempts",
                columns: new[] { "scope_id", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "publication_attempts");
        }
    }
}
