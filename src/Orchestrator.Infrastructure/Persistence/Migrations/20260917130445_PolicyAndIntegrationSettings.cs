using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PolicyAndIntegrationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_settings",
                schema: "orchestrator",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    secret_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    health = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_settings", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "policy_configuration",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    always_requires_approval_csv = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    forbidden_environments_csv = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy_configuration", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_settings",
                schema: "orchestrator");

            migrationBuilder.DropTable(
                name: "policy_configuration",
                schema: "orchestrator");
        }
    }
}
