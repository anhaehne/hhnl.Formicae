using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowExecutionOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelCompletedAt",
                table: "workflows",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelRequestedAt",
                table: "workflows",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPaused",
                table: "workflows",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionAttemptId",
                table: "workflow_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "workflow_logs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "workflow_logs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "workflow_logs",
                type: "text",
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<long>(
                name: "SourceSequence",
                table: "workflow_logs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RuntimeCleanupPending",
                table: "task_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RuntimeLogsCaptured",
                table: "task_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "task_run_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    DefinitionStepId = table.Column<string>(type: "text", nullable: false),
                    LoopIteration = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: true),
                    Output = table.Column<string>(type: "text", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    StructuredOutputsJson = table.Column<string>(type: "text", nullable: true),
                    CustomTaskExecutionJson = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_run_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_run_attempts_workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_logs_WorkflowId_ExecutionAttemptId_Source_SourceSe~",
                table: "workflow_logs",
                columns: new[] { "WorkflowId", "ExecutionAttemptId", "Source", "SourceSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_logs_WorkflowId_Sequence",
                table: "workflow_logs",
                columns: new[] { "WorkflowId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_task_run_attempts_TaskRunId_ExecutionAttemptId",
                table: "task_run_attempts",
                columns: new[] { "TaskRunId", "ExecutionAttemptId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_task_run_attempts_WorkflowId_TaskRunId_AttemptNumber",
                table: "task_run_attempts",
                columns: new[] { "WorkflowId", "TaskRunId", "AttemptNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_run_attempts");

            migrationBuilder.DropIndex(
                name: "IX_workflow_logs_WorkflowId_ExecutionAttemptId_Source_SourceSe~",
                table: "workflow_logs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_logs_WorkflowId_Sequence",
                table: "workflow_logs");

            migrationBuilder.DropColumn(
                name: "CancelCompletedAt",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "CancelRequestedAt",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "IsPaused",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "ExecutionAttemptId",
                table: "workflow_logs");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "workflow_logs");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "workflow_logs");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "workflow_logs");

            migrationBuilder.DropColumn(
                name: "SourceSequence",
                table: "workflow_logs");

            migrationBuilder.DropColumn(
                name: "RuntimeCleanupPending",
                table: "task_runs");

            migrationBuilder.DropColumn(
                name: "RuntimeLogsCaptured",
                table: "task_runs");
        }
    }
}
