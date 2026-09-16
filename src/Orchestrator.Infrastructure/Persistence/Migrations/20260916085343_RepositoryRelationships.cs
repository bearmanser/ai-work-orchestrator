using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepositoryRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "repository_relationships",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relationship_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    evidence_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_verified_commit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    admin_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repository_relationships", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_repository_relationships_source_repository_id",
                schema: "orchestrator",
                table: "repository_relationships",
                column: "source_repository_id");

            migrationBuilder.CreateIndex(
                name: "IX_repository_relationships_target_repository_id",
                schema: "orchestrator",
                table: "repository_relationships",
                column: "target_repository_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "repository_relationships",
                schema: "orchestrator");
        }
    }
}
