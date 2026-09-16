using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorizationEnvelopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "authorization_envelopes",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    repositories = table.Column<string>(type: "jsonb", nullable: false),
                    p1_environments = table.Column<string>(type: "jsonb", nullable: false),
                    p1_companies = table.Column<string>(type: "jsonb", nullable: false),
                    allowed_providers = table.Column<string>(type: "jsonb", nullable: false),
                    allowed_stages = table.Column<string>(type: "jsonb", nullable: false),
                    slack_channels = table.Column<string>(type: "jsonb", nullable: false),
                    contactable_people = table.Column<string>(type: "jsonb", nullable: false),
                    vault_path_prefixes = table.Column<string>(type: "jsonb", nullable: false),
                    network_destinations = table.Column<string>(type: "jsonb", nullable: false),
                    max_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    max_wall_clock = table.Column<TimeSpan>(type: "interval", nullable: false),
                    max_agent_invocations = table.Column<int>(type: "integer", nullable: false),
                    max_correction_attempts = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revocation_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authorization_envelopes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_authorization_envelopes_task_id",
                schema: "orchestrator",
                table: "authorization_envelopes",
                column: "task_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authorization_envelopes",
                schema: "orchestrator");
        }
    }
}
