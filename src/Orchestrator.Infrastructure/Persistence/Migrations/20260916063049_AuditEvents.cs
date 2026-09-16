using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    outcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    details_json = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_action_occurred_at",
                schema: "orchestrator",
                table: "audit_events",
                columns: new[] { "action", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_task_id_occurred_at",
                schema: "orchestrator",
                table: "audit_events",
                columns: new[] { "task_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "orchestrator");
        }
    }
}
