using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudObjectStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "provider_id",
                table: "media",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "local");

            migrationBuilder.CreateTable(
                name: "media_migration_copies",
                columns: table => new
                {
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    target_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    source_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    copied_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_migration_copies", x => x.media_id);
                    table.CheckConstraint("ck_media_migration_copies_state", "state IN ('started', 'verified', 'failed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_migration_copies_scope_id_state",
                table: "media_migration_copies",
                columns: new[] { "scope_id", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_migration_copies");

            migrationBuilder.DropColumn(
                name: "provider_id",
                table: "media");
        }
    }
}
