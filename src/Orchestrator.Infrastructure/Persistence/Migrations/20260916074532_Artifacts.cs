using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Artifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artifacts",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sensitivity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    producer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retain_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "context_snapshots",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sources_json = table.Column<string>(type: "jsonb", nullable: false),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    access_scope = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    token_budget = table.Column<int>(type: "integer", nullable: false),
                    omitted_sources_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_context_snapshots", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_task_id_created_at",
                schema: "orchestrator",
                table: "artifacts",
                columns: new[] { "task_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_context_snapshots_task_id_retrieved_at",
                schema: "orchestrator",
                table: "context_snapshots",
                columns: new[] { "task_id", "retrieved_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifacts",
                schema: "orchestrator");

            migrationBuilder.DropTable(
                name: "context_snapshots",
                schema: "orchestrator");
        }
    }
}
