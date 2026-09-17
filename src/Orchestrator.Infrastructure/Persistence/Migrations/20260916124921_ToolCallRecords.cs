using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ToolCallRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tool_call_records",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    decision = table.Column<string>(type: "jsonb", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_call_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tool_call_records_idempotency_key",
                schema: "orchestrator",
                table: "tool_call_records",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tool_call_records_step_id",
                schema: "orchestrator",
                table: "tool_call_records",
                column: "step_id");

            migrationBuilder.CreateIndex(
                name: "IX_tool_call_records_task_id",
                schema: "orchestrator",
                table: "tool_call_records",
                column: "task_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tool_call_records",
                schema: "orchestrator");
        }
    }
}
