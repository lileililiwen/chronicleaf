using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalGitEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "local_repositories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    root_alias = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    relative_path = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    default_branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_repositories", x => x.id);
                    table.CheckConstraint("ck_local_repositories_relative_path_relative", "relative_path NOT LIKE '/%' AND relative_path NOT LIKE '%/../%' AND relative_path NOT LIKE '%/./%' AND relative_path NOT LIKE '../%' AND relative_path NOT LIKE './%' AND relative_path <> '..' AND relative_path NOT LIKE '%\\\\%' AND relative_path <> ''");
                    table.CheckConstraint("ck_local_repositories_root_alias_relative", "root_alias <> '' AND root_alias NOT LIKE '/%' AND root_alias NOT LIKE '%/%'");
                    table.ForeignKey(
                        name: "fk_local_repositories_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "local_sync_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_bundle_revision = table.Column<long>(type: "bigint", nullable: false),
                    root_alias = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    relative_path = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    head_ref = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    base_ref = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    base_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    command_log = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    commit_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    changed_file_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    truncated = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    excluded_file_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    uncommitted_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_sync_sources", x => x.id);
                    table.CheckConstraint("ck_local_sync_sources_completion", "(completed_at IS NULL AND head_sha IS NULL AND base_sha IS NULL AND command_log IS NULL) OR (completed_at IS NOT NULL AND head_sha IS NOT NULL AND command_log IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_local_sync_sources_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_local_repositories_project_id",
                table: "local_repositories",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_local_repositories_scope_id_project_id",
                table: "local_repositories",
                columns: new[] { "scope_id", "project_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_sync_sources_milestone_id",
                table: "local_sync_sources",
                column: "milestone_id");

            migrationBuilder.CreateIndex(
                name: "ix_local_sync_sources_scope_id_milestone_id_created_at",
                table: "local_sync_sources",
                columns: new[] { "scope_id", "milestone_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_local_sync_sources_scope_id_operation_id",
                table: "local_sync_sources",
                columns: new[] { "scope_id", "operation_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_repositories");

            migrationBuilder.DropTable(
                name: "local_sync_sources");
        }
    }
}
