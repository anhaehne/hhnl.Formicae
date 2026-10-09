using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowEventWaits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow_wait_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    DeliveryId = table.Column<string>(type: "text", nullable: false),
                    EventSequence = table.Column<long>(type: "bigint", nullable: false),
                    EventKey = table.Column<string>(type: "text", nullable: false),
                    Uses = table.Column<string>(type: "text", nullable: false),
                    RepositoryUrl = table.Column<string>(type: "text", nullable: false),
                    IssueUrl = table.Column<string>(type: "text", nullable: false),
                    OutputsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_wait_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_node_waits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Uses = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    RepositoryUrl = table.Column<string>(type: "text", nullable: false),
                    IssueUrl = table.Column<string>(type: "text", nullable: false),
                    EventSequenceFloor = table.Column<long>(type: "bigint", nullable: false),
                    ArmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    InputProvenanceJson = table.Column<string>(type: "text", nullable: true),
                    MatchedEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    MatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsCanceled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_node_waits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_node_waits_task_runs_TaskRunId",
                        column: x => x.TaskRunId,
                        principalTable: "task_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workflow_node_waits_workflow_wait_events_MatchedEventId",
                        column: x => x.MatchedEventId,
                        principalTable: "workflow_wait_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_node_waits_ExecutionAttemptId",
                table: "workflow_node_waits",
                column: "ExecutionAttemptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_node_waits_MatchedEventId",
                table: "workflow_node_waits",
                column: "MatchedEventId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_node_waits_TaskRunId",
                table: "workflow_node_waits",
                column: "TaskRunId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_node_waits_WorkflowId_MatchedEventId",
                table: "workflow_node_waits",
                columns: new[] { "WorkflowId", "MatchedEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_wait_events_Provider_DeliveryId",
                table: "workflow_wait_events",
                columns: new[] { "Provider", "DeliveryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_wait_events_Provider_EventKey",
                table: "workflow_wait_events",
                columns: new[] { "Provider", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_wait_events_Uses_RepositoryUrl_IssueUrl_CreatedAt",
                table: "workflow_wait_events",
                columns: new[] { "Uses", "RepositoryUrl", "IssueUrl", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_node_waits");

            migrationBuilder.DropTable(
                name: "workflow_wait_events");
        }
    }
}
