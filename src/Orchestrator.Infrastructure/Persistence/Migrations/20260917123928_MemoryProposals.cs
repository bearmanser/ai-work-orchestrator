using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MemoryProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "memory_proposals",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vault_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    vault_revision_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_proposals", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_memory_proposals_task_id",
                schema: "orchestrator",
                table: "memory_proposals",
                column: "task_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_proposals",
                schema: "orchestrator");
        }
    }
}
