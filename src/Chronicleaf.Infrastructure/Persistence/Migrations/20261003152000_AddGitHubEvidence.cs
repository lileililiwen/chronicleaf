using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    repo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    credential_ref = table.Column<Guid>(type: "uuid", nullable: true),
                    default_branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    repository_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_connections", x => x.id);
                    table.ForeignKey(
                        name: "fk_github_connections_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sync_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_bundle_revision = table.Column<long>(type: "bigint", nullable: false),
                    owner = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    repo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    head_ref = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    base_ref = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    release_tag = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    issue_number = table.Column<int>(type: "integer", nullable: true),
                    pull_request_number = table.Column<int>(type: "integer", nullable: true),
                    base_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    endpoints = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    commit_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    changed_file_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    truncated = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    excluded_file_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_sources", x => x.id);
                    table.ForeignKey(
                        name: "fk_sync_sources_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_github_connections_project_id",
                table: "github_connections",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_github_connections_scope_id_project_id",
                table: "github_connections",
                columns: new[] { "scope_id", "project_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_sources_milestone_id",
                table: "sync_sources",
                column: "milestone_id");

            migrationBuilder.CreateIndex(
                name: "ix_sync_sources_scope_id_milestone_id_created_at",
                table: "sync_sources",
                columns: new[] { "scope_id", "milestone_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_sync_sources_scope_id_operation_id",
                table: "sync_sources",
                columns: new[] { "scope_id", "operation_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_connections");

            migrationBuilder.DropTable(
                name: "sync_sources");
        }
    }
}
