using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgencyClientProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    notes = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    context_revision = table.Column<long>(type: "bigint", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clients", x => x.id);
                    table.CheckConstraint(
                        "ck_clients_revision_nonnegative",
                        "revision >= 0");
                    table.CheckConstraint(
                        "ck_clients_context_revision_nonnegative",
                        "context_revision >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_clients_scope_id_name",
                table: "clients",
                columns: new[] { "scope_id", "name" });

            // One optional client per project: NULL stays a personal, unassigned project, so no
            // backfill is needed - every existing row already satisfies the new column.
            migrationBuilder.AddColumn<Guid>(
                name: "client_id",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_client_id",
                table: "projects",
                column: "client_id");

            migrationBuilder.AddForeignKey(
                name: "fk_projects_client_id",
                table: "projects",
                column: "client_id",
                principalTable: "clients",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_projects_client_id",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "IX_projects_client_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "client_id",
                table: "projects");

            migrationBuilder.DropTable(
                name: "clients");
        }
    }
}
