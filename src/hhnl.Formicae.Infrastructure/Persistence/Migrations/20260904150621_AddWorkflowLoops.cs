using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowLoops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_task_runs_WorkflowId_Kind",
                table: "task_runs");

            migrationBuilder.AddColumn<string>(
                name: "CurrentDefinitionStepId",
                table: "workflows",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefinitionStepId",
                table: "task_runs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "LoopIteration",
                table: "task_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "workflow_loop_iterations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoopId = table.Column<string>(type: "text", nullable: false),
                    IterationNumber = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_loop_iterations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_loop_iterations_workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_task_runs_WorkflowId_DefinitionStepId_LoopIteration",
                table: "task_runs",
                columns: new[] { "WorkflowId", "DefinitionStepId", "LoopIteration" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_task_runs_WorkflowId_Kind_CreatedAt",
                table: "task_runs",
                columns: new[] { "WorkflowId", "Kind", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_loop_iterations_WorkflowId_LoopId_IterationNumber",
                table: "workflow_loop_iterations",
                columns: new[] { "WorkflowId", "LoopId", "IterationNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_loop_iterations");

            migrationBuilder.DropIndex(
                name: "IX_task_runs_WorkflowId_DefinitionStepId_LoopIteration",
                table: "task_runs");

            migrationBuilder.DropIndex(
                name: "IX_task_runs_WorkflowId_Kind_CreatedAt",
                table: "task_runs");

            migrationBuilder.DropColumn(
                name: "CurrentDefinitionStepId",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "DefinitionStepId",
                table: "task_runs");

            migrationBuilder.DropColumn(
                name: "LoopIteration",
                table: "task_runs");

            migrationBuilder.CreateIndex(
                name: "IX_task_runs_WorkflowId_Kind",
                table: "task_runs",
                columns: new[] { "WorkflowId", "Kind" },
                unique: true);
        }
    }
}
