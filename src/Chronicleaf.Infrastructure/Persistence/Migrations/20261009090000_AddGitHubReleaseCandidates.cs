using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubReleaseCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_candidate_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_project_revision = table.Column<long>(type: "bigint", nullable: false),
                    owner = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    repo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    endpoints = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    release_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    tag_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    candidate_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    recorded_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_candidate_runs", x => x.id);
                    table.CheckConstraint("ck_github_candidate_runs_completion", "(completed_at IS NULL AND fetched_at IS NULL AND endpoints IS NULL) OR (completed_at IS NOT NULL AND fetched_at IS NOT NULL AND endpoints IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_github_candidate_runs_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "github_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    delivery_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_candidates", x => x.id);
                    table.CheckConstraint("ck_github_candidates_kind", "kind IN ('release', 'tag', 'pull-request', 'issue', 'star')");
                    table.CheckConstraint("ck_github_candidates_source", "source IN ('webhook', 'poll')");
                    table.CheckConstraint("ck_github_candidates_source_key", "source_key LIKE 'release:%' OR source_key LIKE 'tag:%' OR source_key LIKE 'event:%'");
                    table.ForeignKey(
                        name: "fk_github_candidates_connection_id",
                        column: x => x.connection_id,
                        principalTable: "github_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_github_candidates_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "github_webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposals = table.Column<string>(type: "jsonb", nullable: false),
                    delivery_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(40)", maxLength: 40, nullable: false),
                    action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    reason = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    candidate_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_webhook_deliveries", x => x.id);
                    table.CheckConstraint("ck_github_webhook_deliveries_outcome", "(status = 'pending' AND candidate_count = 0) OR (status = 'recorded' AND candidate_count > 0 AND reason IS NULL) OR (status = 'ignored' AND candidate_count = 0 AND reason IS NOT NULL)");
                    table.CheckConstraint("ck_github_webhook_deliveries_reason", "reason IS NULL OR reason IN ('unsupported-event', 'unsupported-action', 'below-star-threshold', 'connection-inactive', 'nothing-named')");
                    table.CheckConstraint("ck_github_webhook_deliveries_status", "status IN ('pending', 'recorded', 'ignored')");
                    table.ForeignKey(
                        name: "fk_github_webhook_deliveries_connection_id",
                        column: x => x.connection_id,
                        principalTable: "github_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "github_webhook_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_webhook_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_github_webhook_subscriptions_connection_id",
                        column: x => x.connection_id,
                        principalTable: "github_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_github_webhook_subscriptions_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_github_candidate_runs_project_id",
                table: "github_candidate_runs",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_github_candidate_runs_scope_id_project_id_created_at",
                table: "github_candidate_runs",
                columns: new[] { "scope_id", "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_github_candidate_runs_scope_id_operation_id",
                table: "github_candidate_runs",
                columns: new[] { "scope_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_github_candidates_connection_id",
                table: "github_candidates",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "IX_github_candidates_project_id",
                table: "github_candidates",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_github_candidates_scope_id_project_id_received_at",
                table: "github_candidates",
                columns: new[] { "scope_id", "project_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ux_github_candidates_scope_id_source_key",
                table: "github_candidates",
                columns: new[] { "scope_id", "source_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_github_webhook_deliveries_scope_id_connection_received_at",
                table: "github_webhook_deliveries",
                columns: new[] { "scope_id", "connection_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_github_webhook_deliveries_scope_id_id",
                table: "github_webhook_deliveries",
                columns: new[] { "scope_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_github_webhook_deliveries_connection_id_delivery_id",
                table: "github_webhook_deliveries",
                columns: new[] { "connection_id", "delivery_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_github_webhook_subscriptions_project_id",
                table: "github_webhook_subscriptions",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_github_webhook_subscriptions_scope_id_project_id",
                table: "github_webhook_subscriptions",
                columns: new[] { "scope_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ux_github_webhook_subscriptions_connection_id",
                table: "github_webhook_subscriptions",
                column: "connection_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_candidate_runs");

            migrationBuilder.DropTable(
                name: "github_candidates");

            migrationBuilder.DropTable(
                name: "github_webhook_deliveries");

            migrationBuilder.DropTable(
                name: "github_webhook_subscriptions");
        }
    }
}
