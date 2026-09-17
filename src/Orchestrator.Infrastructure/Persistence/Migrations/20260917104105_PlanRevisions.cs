using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlanRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plan_revisions",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    content_json = table.Column<string>(type: "jsonb", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    confirmation_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    confirmed_by_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    superseded_by_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    context_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_revisions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plan_revisions_content_hash",
                schema: "orchestrator",
                table: "plan_revisions",
                column: "content_hash");

            migrationBuilder.CreateIndex(
                name: "IX_plan_revisions_task_id_revision_number",
                schema: "orchestrator",
                table: "plan_revisions",
                columns: new[] { "task_id", "revision_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plan_revisions",
                schema: "orchestrator");
        }
    }
}
