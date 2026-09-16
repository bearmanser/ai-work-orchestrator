using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Budgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budgets",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    max_usd = table.Column<decimal>(type: "numeric", nullable: false),
                    max_wall_clock = table.Column<TimeSpan>(type: "interval", nullable: false),
                    max_agent_invocations = table.Column<int>(type: "integer", nullable: false),
                    max_correction_attempts = table.Column<int>(type: "integer", nullable: false),
                    spent_usd = table.Column<decimal>(type: "numeric", nullable: false),
                    elapsed_wall_clock = table.Column<TimeSpan>(type: "interval", nullable: false),
                    agent_invocations = table.Column<int>(type: "integer", nullable: false),
                    correction_attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budgets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_budgets_task_id",
                schema: "orchestrator",
                table: "budgets",
                column: "task_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budgets",
                schema: "orchestrator");
        }
    }
}
