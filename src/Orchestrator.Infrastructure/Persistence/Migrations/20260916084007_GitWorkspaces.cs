using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GitWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspaces",
                schema: "orchestrator",
                columns: table => new
                {
                    workspace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    baseline_commit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    base_branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    work_branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    credential_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspaces", x => x.workspace_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_task_id",
                schema: "orchestrator",
                table: "workspaces",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_task_id_repository_id",
                schema: "orchestrator",
                table: "workspaces",
                columns: new[] { "task_id", "repository_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspaces",
                schema: "orchestrator");
        }
    }
}
