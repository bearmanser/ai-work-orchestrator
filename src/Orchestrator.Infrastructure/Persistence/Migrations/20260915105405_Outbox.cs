using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Outbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "State",
                schema: "orchestrator",
                table: "tasks",
                newName: "state");

            migrationBuilder.RenameColumn(
                name: "Source",
                schema: "orchestrator",
                table: "tasks",
                newName: "source");

            migrationBuilder.RenameColumn(
                name: "Objective",
                schema: "orchestrator",
                table: "tasks",
                newName: "objective");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "orchestrator",
                table: "tasks",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                schema: "orchestrator",
                table: "tasks",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "RequesterId",
                schema: "orchestrator",
                table: "tasks",
                newName: "requester_id");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "orchestrator",
                table: "tasks",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "CorrelationId",
                schema: "orchestrator",
                table: "tasks",
                newName: "correlation_id");

            migrationBuilder.RenameIndex(
                name: "IX_tasks_State",
                schema: "orchestrator",
                table: "tasks",
                newName: "IX_tasks_state");

            migrationBuilder.RenameIndex(
                name: "IX_tasks_CorrelationId",
                schema: "orchestrator",
                table: "tasks",
                newName: "IX_tasks_correlation_id");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_idempotency_key",
                schema: "orchestrator",
                table: "outbox_messages",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_processed_at_next_attempt_at",
                schema: "orchestrator",
                table: "outbox_messages",
                columns: new[] { "processed_at", "next_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "orchestrator");

            migrationBuilder.RenameColumn(
                name: "state",
                schema: "orchestrator",
                table: "tasks",
                newName: "State");

            migrationBuilder.RenameColumn(
                name: "source",
                schema: "orchestrator",
                table: "tasks",
                newName: "Source");

            migrationBuilder.RenameColumn(
                name: "objective",
                schema: "orchestrator",
                table: "tasks",
                newName: "Objective");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "orchestrator",
                table: "tasks",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                schema: "orchestrator",
                table: "tasks",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "requester_id",
                schema: "orchestrator",
                table: "tasks",
                newName: "RequesterId");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "orchestrator",
                table: "tasks",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "correlation_id",
                schema: "orchestrator",
                table: "tasks",
                newName: "CorrelationId");

            migrationBuilder.RenameIndex(
                name: "IX_tasks_state",
                schema: "orchestrator",
                table: "tasks",
                newName: "IX_tasks_State");

            migrationBuilder.RenameIndex(
                name: "IX_tasks_correlation_id",
                schema: "orchestrator",
                table: "tasks",
                newName: "IX_tasks_CorrelationId");
        }
    }
}
