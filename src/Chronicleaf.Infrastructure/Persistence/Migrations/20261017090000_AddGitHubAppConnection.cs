using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubAppConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "auth_mode",
                table: "github_connections",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "pat");

            migrationBuilder.AddColumn<long>(
                name: "installation_id",
                table: "github_connections",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "github_app_installations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    account_login = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    repository_selection = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_app_installations", x => x.id);
                    table.CheckConstraint("ck_github_app_installations_repository_selection", "repository_selection IN ('all','selected')");
                    table.CheckConstraint("ck_github_app_installations_status", "status IN ('active','suspended','removed')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_github_connections_auth_mode",
                table: "github_connections",
                sql: "auth_mode IN ('pat','installation')");

            migrationBuilder.CreateIndex(
                name: "ix_github_app_installations_scope_id",
                table: "github_app_installations",
                column: "scope_id");

            migrationBuilder.CreateIndex(
                name: "ux_github_app_installations_installation_id",
                table: "github_app_installations",
                column: "installation_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_app_installations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_github_connections_auth_mode",
                table: "github_connections");

            migrationBuilder.DropColumn(
                name: "auth_mode",
                table: "github_connections");

            migrationBuilder.DropColumn(
                name: "installation_id",
                table: "github_connections");
        }
    }
}
