using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamProjectPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "tenant_memberships",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "project_memberships",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_memberships", x => new { x.project_id, x.user_id, x.permission });
                    table.CheckConstraint("ck_project_memberships_permission_known", "permission IN ('reader', 'editor', 'approver', 'publisher')");
                    table.CheckConstraint("ck_project_memberships_revision_nonnegative", "revision >= 0");
                    table.ForeignKey(
                        name: "fk_project_memberships_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_memberships_user_id",
                        column: x => x.user_id,
                        principalTable: "hosted_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_invitations", x => x.id);
                    table.CheckConstraint("ck_tenant_invitations_role_known", "role IN ('owner', 'editor', 'viewer')");
                    table.ForeignKey(
                        name: "fk_tenant_invitations_created_by",
                        column: x => x.created_by,
                        principalTable: "hosted_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tenant_invitations_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_memberships_revision_nonnegative",
                table: "tenant_memberships",
                sql: "revision >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_project_memberships_project_id",
                table: "project_memberships",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_memberships_user_id",
                table: "project_memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_invitations_created_by",
                table: "tenant_invitations",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_invitations_tenant_id_email",
                table: "tenant_invitations",
                columns: new[] { "tenant_id", "email" },
                unique: true,
                filter: "accepted_at IS NULL AND revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_invitations_token_hash",
                table: "tenant_invitations",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_memberships");

            migrationBuilder.DropTable(
                name: "tenant_invitations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_memberships_revision_nonnegative",
                table: "tenant_memberships");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "tenant_memberships");
        }
    }
}
