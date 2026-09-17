using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PeopleAndOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ownership_entries",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    owner_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    can_confirm_plans = table.Column<bool>(type: "boolean", nullable: false),
                    can_approve_restricted_actions = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ownership_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "people",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    slack_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    azure_dev_ops_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    email = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_people", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ownership_entries_subject_subject_key",
                schema: "orchestrator",
                table: "ownership_entries",
                columns: new[] { "subject", "subject_key" });

            migrationBuilder.CreateIndex(
                name: "IX_people_slack_user_id",
                schema: "orchestrator",
                table: "people",
                column: "slack_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ownership_entries",
                schema: "orchestrator");

            migrationBuilder.DropTable(
                name: "people",
                schema: "orchestrator");
        }
    }
}
