using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PullRequestTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pull_request_tracking",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pull_request_id = table.Column<int>(type: "integer", nullable: false),
                    url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    current_head_commit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_reconciled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_approved = table.Column<bool>(type: "boolean", nullable: false),
                    Feedback = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pull_request_tracking", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pull_request_tracking_task_id",
                schema: "orchestrator",
                table: "pull_request_tracking",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "IX_pull_request_tracking_task_id_repository_id",
                schema: "orchestrator",
                table: "pull_request_tracking",
                columns: new[] { "task_id", "repository_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pull_request_tracking",
                schema: "orchestrator");
        }
    }
}
