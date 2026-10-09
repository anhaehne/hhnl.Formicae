using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowControlCycles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_parallel_executions_WorkflowId_NodeId",
                table: "workflow_parallel_executions");

            migrationBuilder.DropIndex(
                name: "IX_workflow_decision_executions_WorkflowId_NodeId",
                table: "workflow_decision_executions");

            migrationBuilder.AddColumn<string>(
                name: "CycleExecutionJson",
                table: "workflows",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VisitIteration",
                table: "workflow_parallel_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VisitIteration",
                table: "workflow_decision_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_parallel_executions_WorkflowId_NodeId_VisitIterati~",
                table: "workflow_parallel_executions",
                columns: new[] { "WorkflowId", "NodeId", "VisitIteration" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_decision_executions_WorkflowId_NodeId_VisitIterati~",
                table: "workflow_decision_executions",
                columns: new[] { "WorkflowId", "NodeId", "VisitIteration" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_parallel_executions_WorkflowId_NodeId_VisitIterati~",
                table: "workflow_parallel_executions");

            migrationBuilder.DropIndex(
                name: "IX_workflow_decision_executions_WorkflowId_NodeId_VisitIterati~",
                table: "workflow_decision_executions");

            migrationBuilder.DropColumn(
                name: "CycleExecutionJson",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "VisitIteration",
                table: "workflow_parallel_executions");

            migrationBuilder.DropColumn(
                name: "VisitIteration",
                table: "workflow_decision_executions");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_parallel_executions_WorkflowId_NodeId",
                table: "workflow_parallel_executions",
                columns: new[] { "WorkflowId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_decision_executions_WorkflowId_NodeId",
                table: "workflow_decision_executions",
                columns: new[] { "WorkflowId", "NodeId" },
                unique: true);
        }
    }
}
