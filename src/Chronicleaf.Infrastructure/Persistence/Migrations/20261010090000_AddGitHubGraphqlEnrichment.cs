using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicleaf.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubGraphqlEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_enrichment_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_bundle_revision = table.Column<long>(type: "bigint", nullable: false),
                    owner = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    repo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_enrichment_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_github_enrichment_runs_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_github_enrichment_runs_milestone_id",
                table: "github_enrichment_runs",
                column: "milestone_id");

            migrationBuilder.CreateIndex(
                name: "ix_github_enrichment_runs_scope_id_milestone_id_created_at",
                table: "github_enrichment_runs",
                columns: new[] { "scope_id", "milestone_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_github_enrichment_runs_scope_id_operation_id",
                table: "github_enrichment_runs",
                columns: new[] { "scope_id", "operation_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_enrichment_runs");
        }
    }
}
