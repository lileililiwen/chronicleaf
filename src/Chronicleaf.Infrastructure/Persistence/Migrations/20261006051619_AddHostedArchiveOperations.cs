using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHostedArchiveOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "archive_backup_manifests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    db_backup_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    media_inventory_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    content_digest = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    encryption_key_version = table.Column<int>(type: "integer", nullable: false),
                    restore_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_archive_backup_manifests", x => x.id);
                    table.CheckConstraint("ck_archive_backup_manifests_key", "encryption_key_version >= 1");
                    table.CheckConstraint("ck_archive_backup_manifests_revision", "revision >= 1");
                    table.CheckConstraint("ck_archive_backup_manifests_schema", "schema_version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "archive_tombstones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_name = table.Column<string>(type: "text", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recovery_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    erase_requested = table.Column<bool>(type: "boolean", nullable: false),
                    hold_reason = table.Column<string>(type: "text", nullable: true),
                    restored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_archive_tombstones", x => x.id);
                    table.CheckConstraint("ck_archive_tombstones_revision", "revision >= 1");
                    table.CheckConstraint("ck_archive_tombstones_window", "recovery_until > deleted_at");
                    table.ForeignKey(
                        name: "fk_archive_tombstones_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_archive_backup_manifests_scope_id_created_at",
                table: "archive_backup_manifests",
                columns: new[] { "scope_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_archive_tombstones_project_id",
                table: "archive_tombstones",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_tombstones_recovery_until",
                table: "archive_tombstones",
                column: "recovery_until");

            migrationBuilder.CreateIndex(
                name: "ux_archive_tombstones_scope_id_project_id_live",
                table: "archive_tombstones",
                columns: new[] { "scope_id", "project_id" },
                unique: true,
                filter: "restored_at IS NULL AND purged_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archive_backup_manifests");

            migrationBuilder.DropTable(
                name: "archive_tombstones");
        }
    }
}
