using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkedInCopyPackage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "linkedin_copy_media",
                columns: table => new
                {
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alt_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_linkedin_copy_media", x => new { x.asset_id, x.revision, x.media_id });
                    table.CheckConstraint("ck_linkedin_copy_media_revision_positive", "revision >= 1");
                    table.CheckConstraint("ck_linkedin_copy_media_alt_text_present", "length(alt_text) >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_linkedin_copy_media_scope_id_asset_id_revision",
                table: "linkedin_copy_media",
                columns: new[] { "scope_id", "asset_id", "revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "linkedin_copy_media");
        }
    }
}
