using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowParallelExecutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionAttemptId",
                table: "task_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "workflow_parallel_executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "text", nullable: false),
                    EntryPlanArtifact = table.Column<string>(type: "text", nullable: true),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_parallel_executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_parallel_executions_workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_parallel_executions_WorkflowId_NodeId",
                table: "workflow_parallel_executions",
                columns: new[] { "WorkflowId", "NodeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_parallel_executions");

            migrationBuilder.DropColumn(
                name: "ExecutionAttemptId",
                table: "task_runs");
        }
    }
}
