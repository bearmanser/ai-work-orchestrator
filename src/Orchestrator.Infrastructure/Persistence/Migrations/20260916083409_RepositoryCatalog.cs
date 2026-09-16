using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepositoryCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "repositories",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    remote_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    default_branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    purpose = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    owner_contact_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    access_policy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    restore = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    build = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    test = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    lint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    working_directory = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    timeout = table.Column<TimeSpan>(type: "interval", nullable: false),
                    instruction_file_paths_json = table.Column<string>(type: "jsonb", nullable: false),
                    last_inspected_commit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_inspected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repositories", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_repositories_name",
                schema: "orchestrator",
                table: "repositories",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_repositories_remote_url",
                schema: "orchestrator",
                table: "repositories",
                column: "remote_url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "repositories",
                schema: "orchestrator");
        }
    }
}
