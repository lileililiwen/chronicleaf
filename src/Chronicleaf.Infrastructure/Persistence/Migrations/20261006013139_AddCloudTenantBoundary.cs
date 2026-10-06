using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudTenantBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hosted_login_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    nonce_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code_verifier = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hosted_login_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hosted_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hosted_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id);
                    table.CheckConstraint("ck_tenants_revision_nonnegative", "revision >= 0");
                });

            migrationBuilder.CreateTable(
                name: "hosted_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    active_tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idle_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    antiforgery_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hosted_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_hosted_sessions_user_id",
                        column: x => x.user_id,
                        principalTable: "hosted_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hosted_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hosted_imports", x => x.id);
                    table.ForeignKey(
                        name: "fk_hosted_imports_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_memberships",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_memberships", x => new { x.tenant_id, x.user_id });
                    table.CheckConstraint("ck_tenant_memberships_role_known", "role IN ('owner', 'editor', 'viewer')");
                    table.ForeignKey(
                        name: "fk_tenant_memberships_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tenant_memberships_user_id",
                        column: x => x.user_id,
                        principalTable: "hosted_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_scopes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_scopes", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenant_scopes_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_hosted_imports_tenant_id",
                table: "hosted_imports",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_hosted_imports_installation_id_tenant_id",
                table: "hosted_imports",
                columns: new[] { "installation_id", "tenant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_hosted_login_requests_state_hash",
                table: "hosted_login_requests",
                column: "state_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_hosted_sessions_user_id",
                table: "hosted_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_hosted_sessions_id_hash",
                table: "hosted_sessions",
                column: "id_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_hosted_users_issuer_subject",
                table: "hosted_users",
                columns: new[] { "issuer", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_memberships_user_id",
                table: "tenant_memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_scopes_tenant_id",
                table: "tenant_scopes",
                column: "tenant_id",
                unique: true);

            // Row-level security on the tenant tables. The application connects as the table
            // owner, which bypasses policies by default, so FORCE is what makes the policies
            // apply to the application's own pooled connections as well. A transaction that
            // carries no app.tenant_id - every Community transaction, and any Hosted read
            // outside a tenant context - sees every row, exactly as before; a transaction that
            // carries one sees only that tenant's rows. The tenancy service sets the value with
            // SET LOCAL inside its own unit of work, so it dies with the transaction rather than
            // leaking into the connection pool's next borrower.
            //
            // The unset test is nullif rather than IS NULL for one observed reason: a custom
            // variable first touched by SET LOCAL keeps an empty session value afterwards instead
            // of reading NULL again, so a later transaction on the same pooled connection reads
            // '' rather than NULL. An empty value can never be a tenant id - a uuid renders
            // lower-case hex, never empty - so treating it as unset is exact rather than lax, and
            // a leaked tenant id could never read as unset either way.
            migrationBuilder.Sql(
                """
                ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON tenants FOR ALL
                    USING (nullif(current_setting('app.tenant_id', true), '') IS NULL OR id::text = current_setting('app.tenant_id', true))
                    WITH CHECK (nullif(current_setting('app.tenant_id', true), '') IS NULL OR id::text = current_setting('app.tenant_id', true));

                ALTER TABLE tenant_memberships ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenant_memberships FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON tenant_memberships FOR ALL
                    USING (nullif(current_setting('app.tenant_id', true), '') IS NULL OR tenant_id::text = current_setting('app.tenant_id', true))
                    WITH CHECK (nullif(current_setting('app.tenant_id', true), '') IS NULL OR tenant_id::text = current_setting('app.tenant_id', true));

                ALTER TABLE tenant_scopes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenant_scopes FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON tenant_scopes FOR ALL
                    USING (nullif(current_setting('app.tenant_id', true), '') IS NULL OR tenant_id::text = current_setting('app.tenant_id', true))
                    WITH CHECK (nullif(current_setting('app.tenant_id', true), '') IS NULL OR tenant_id::text = current_setting('app.tenant_id', true));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON tenant_scopes;
                ALTER TABLE tenant_scopes NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE tenant_scopes DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS tenant_isolation ON tenant_memberships;
                ALTER TABLE tenant_memberships NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE tenant_memberships DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS tenant_isolation ON tenants;
                ALTER TABLE tenants NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE tenants DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropTable(
                name: "hosted_imports");

            migrationBuilder.DropTable(
                name: "hosted_login_requests");

            migrationBuilder.DropTable(
                name: "hosted_sessions");

            migrationBuilder.DropTable(
                name: "tenant_memberships");

            migrationBuilder.DropTable(
                name: "tenant_scopes");

            migrationBuilder.DropTable(
                name: "hosted_users");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
