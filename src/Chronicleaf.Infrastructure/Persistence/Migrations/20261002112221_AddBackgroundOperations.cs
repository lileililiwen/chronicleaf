using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The queue's schema is created here so the database this migration produces is a
            // complete, named home for both the application's tables and the engine's. The engine's
            // own tables are not written into this migration: Hangfire.PostgreSql creates them inside
            // this schema on first use, and a hand-copied snapshot of another project's DDL would be
            // a second source of truth that silently rots on the next engine upgrade.
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS hangfire;");

            migrationBuilder.CreateTable(
                name: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_revision = table.Column<long>(type: "bigint", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    result_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_recoveries = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_operations_scope_id_created_at_id",
                table: "operations",
                columns: new[] { "scope_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_operations_status_lease_expires_at",
                table: "operations",
                columns: new[] { "status", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ux_operations_scope_id_kind_idempotency_key",
                table: "operations",
                columns: new[] { "scope_id", "kind", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operations");

            // CASCADE removes whatever the engine created inside the schema, so a rollback leaves
            // neither the operations table nor an orphaned queue schema behind.
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS hangfire CASCADE;");
        }
    }
}
