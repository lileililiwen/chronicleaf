using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgencyWeeklyReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "weekly_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_start = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    window_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    project_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    attribution_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_refs = table.Column<string>(type: "text", nullable: false),
                    inputs_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    body_markdown = table.Column<string>(type: "text", nullable: false),
                    body_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    in_progress_notes = table.Column<string>(type: "text", nullable: false),
                    next_week_notes = table.Column<string>(type: "text", nullable: false),
                    approved_revision = table.Column<long>(type: "bigint", nullable: true),
                    approved_body_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    approved_attribution_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    approved_source_refs = table.Column<string>(type: "text", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_weekly_reports", x => x.id);
                    table.CheckConstraint("ck_weekly_reports_revision_positive", "revision >= 1");
                    table.CheckConstraint("ck_weekly_reports_week_start_shape", "week_start ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'");
                    table.CheckConstraint("ck_weekly_reports_window_ordered", "window_end_utc > window_start_utc");
                    table.CheckConstraint("ck_weekly_reports_attribution_hash_sha256_hex", "attribution_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_weekly_reports_inputs_hash_sha256_hex", "inputs_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_weekly_reports_body_hash_sha256_hex", "body_hash ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "fk_weekly_reports_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_weekly_reports_scope_id_client_id_week_start_revision",
                table: "weekly_reports",
                columns: new[] { "scope_id", "client_id", "week_start", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_weekly_reports_scope_id_idempotency_key",
                table: "weekly_reports",
                columns: new[] { "scope_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_weekly_reports_scope_id_client_id_week_start",
                table: "weekly_reports",
                columns: new[] { "scope_id", "client_id", "week_start" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "weekly_reports");
        }
    }
}
