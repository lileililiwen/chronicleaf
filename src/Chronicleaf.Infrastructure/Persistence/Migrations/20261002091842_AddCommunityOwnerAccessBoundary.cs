using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommunityOwnerAccessBoundary : Migration
    {
        /// <summary>
        /// The Community access boundary's own forward migration (COMMUNITY-ACCESS-001).
        /// </summary>
        /// <remarks>
        /// It adds the three tables this package owns - <c>session</c>, <c>login_attempt</c> and
        /// <c>access_state</c> - and the <c>installation.owner_actor_id</c> column, and it is
        /// additive: the three data-foundation tables and their rows are untouched.
        /// <para>
        /// The column is backfilled here, before anything enforces it, so an installation that was
        /// already provisioned by the previous build gains a stable owner identity as part of the
        /// upgrade instead of waiting for the first request. <c>gen_random_uuid()</c> is part of
        /// core PostgreSQL since 13, so no extension has to be installed.
        /// </para>
        /// <para>
        /// <see cref="Down"/> is destructive: it drops the access tables and the column. Nothing
        /// runs it by itself - ordinary start-up never migrates - and rolling the feature back is a
        /// configuration decision, because a build that does not know about these tables has no
        /// access boundary to keep.
        /// </para>
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "owner_actor_id",
                table: "installation",
                type: "uuid",
                nullable: true);

            // Backfilled before enforcement: every existing installation row gets its one owner
            // identity here, and no later step may change it.
            migrationBuilder.Sql(
                """
                UPDATE installation
                SET owner_actor_id = gen_random_uuid()
                WHERE owner_actor_id IS NULL;
                """);

            migrationBuilder.CreateTable(
                name: "access_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    token_hash_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_state", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "login_attempt",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_attempt", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "session",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idle_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    antiforgery_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_login_attempt_client_occurred_at",
                table: "login_attempt",
                columns: new[] { "client_key_hash", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_session_actor_id",
                table: "session",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ux_session_id_hash",
                table: "session",
                column: "id_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_state");

            migrationBuilder.DropTable(
                name: "login_attempt");

            migrationBuilder.DropTable(
                name: "session");

            migrationBuilder.DropColumn(
                name: "owner_actor_id",
                table: "installation");
        }
    }
}
