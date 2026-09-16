using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HumanQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "human_questions",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    channel_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    thread_ts = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    posted_message_ts = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    remind_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    escalate_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    suspend_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    answer_text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    answered_by_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_human_questions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_human_questions_channel_id_thread_ts",
                schema: "orchestrator",
                table: "human_questions",
                columns: new[] { "channel_id", "thread_ts" });

            migrationBuilder.CreateIndex(
                name: "IX_human_questions_status",
                schema: "orchestrator",
                table: "human_questions",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "human_questions",
                schema: "orchestrator");
        }
    }
}
