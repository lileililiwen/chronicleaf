using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The Project and Milestone package's own forward migration
    /// (PROJECTS-AND-MILESTONES-001, PROJECTS-AND-MILESTONES-002).
    /// </summary>
    /// <remarks>
    /// It adds the two tables this package owns - <c>projects</c> and <c>milestones</c> - and it is
    /// purely additive: the data foundation's tables and the access boundary's tables are untouched,
    /// and no existing row is rewritten.
    /// <para>
    /// Three properties of the schema are the contract, not incidental structure. The unique index
    /// on <c>(project_id, source, source_key)</c> is <em>filtered</em> on
    /// <c>source_key IS NOT NULL</c>, which is what makes a repeated import of the same upstream
    /// record idempotent while leaving the owner's own records, which carry no key, free of any
    /// uniqueness constraint. The <c>revision</c> columns are the optimistic concurrency token every
    /// edit presents, and nothing in the stores ever writes a revision value it computed itself.
    /// The nullable <c>archived_at</c> is a soft archive: it makes a project read-only and keeps
    /// every row beneath it.
    /// </para>
    /// <para>
    /// There is deliberately no foreign key from <c>milestones</c> to <c>projects</c>, matching the
    /// style of the two migrations before it, which also leave their target ids unconstrained. The
    /// stores resolve a milestone through its project on every read and every write, so the
    /// relationship is enforced where it is used; a restrictive foreign key belongs with the
    /// retention package, which is the only thing that will ever own erasing a record.
    /// </para>
    /// <para>
    /// <see cref="Down"/> drops both tables and is destructive, so it is not a rollback of a
    /// feature. Ordinary start-up never migrates, and rolling this package back means not serving its
    /// routes: the archived history a user has already created is not a thing to be dropped to undo
    /// a deployment.
    /// </para>
    /// </remarks>
    public partial class AddProjectsAndMilestones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "milestones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    notes = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    known_limitations = table.Column<string[]>(type: "text[]", nullable: false),
                    next_steps = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_milestones", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    repo_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_milestones_project_id_occurred_at_id",
                table: "milestones",
                columns: new[] { "project_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_milestones_project_id_source_source_key",
                table: "milestones",
                columns: new[] { "project_id", "source", "source_key" },
                unique: true,
                filter: "source_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_projects_scope_id_name",
                table: "projects",
                columns: new[] { "scope_id", "name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "milestones");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
