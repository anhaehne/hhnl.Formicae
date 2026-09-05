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

            migrationBuilder.Sql("-- Pre-loop retries updated the same (WorkflowId, Kind) row. Preserve that row and all history.\r\n-- Resolve only against the pinned immutable version, never the current/default definition.\r\nCREATE TEMP TABLE legacy_task_step_map ON COMMIT DROP AS\r\nWITH required_keys AS (\r\n    SELECT \"WorkflowId\", \"Kind\" FROM task_runs\r\n    UNION\r\n    SELECT \"Id\", \"CurrentStep\" FROM workflows WHERE \"CurrentStep\" NOT IN ('None', 'Done')\r\n), builtins(kind, uses, canonical_id) AS (\r\n    VALUES ('Plan', 'builtins.plan', 'plan'),\r\n           ('Implement', 'builtins.implement', 'implement'),\r\n           ('CreatePullRequest', 'builtins.create-pull-request', 'createPullRequest'),\r\n           ('AddressComments', 'builtins.address-comments', 'addressComments')\r\n)\r\nSELECT keys.\"WorkflowId\", keys.\"Kind\",\r\n       CASE WHEN w.\"WorkflowDefinitionVersionId\" IS NULL THEN b.canonical_id\r\n            ELSE matched.step_id END AS step_id,\r\n       CASE WHEN w.\"Id\" IS NULL OR b.kind IS NULL THEN 0\r\n            WHEN w.\"WorkflowDefinitionVersionId\" IS NULL THEN 1\r\n            ELSE matched.matches END AS matches\r\nFROM required_keys keys\r\nLEFT JOIN workflows w ON w.\"Id\" = keys.\"WorkflowId\"\r\nLEFT JOIN builtins b ON b.kind = keys.\"Kind\"\r\nLEFT JOIN workflow_definition_versions v ON v.\"Id\" = w.\"WorkflowDefinitionVersionId\"\r\nLEFT JOIN LATERAL (\r\n    SELECT count(*) AS matches, min(step->>'id') AS step_id\r\n    FROM jsonb_array_elements(v.\"DefinitionJson\"::jsonb->'steps') step\r\n    WHERE step->>'uses' = b.uses\r\n) matched ON true;\r\n\r\nDO $$\r\nDECLARE invalid record;\r\nBEGIN\r\n    SELECT * INTO invalid FROM legacy_task_step_map\r\n    WHERE matches <> 1 OR step_id IS NULL OR btrim(step_id) = '' LIMIT 1;\r\n    IF FOUND THEN\r\n        RAISE EXCEPTION 'Cannot normalize legacy workflow %, kind %: expected one nonempty definition step, found %',\r\n            invalid.\"WorkflowId\", invalid.\"Kind\", invalid.matches;\r\n    END IF;\r\nEND $$;\r\n\r\nUPDATE task_runs r SET \"DefinitionStepId\" = m.step_id\r\nFROM legacy_task_step_map m\r\nWHERE r.\"WorkflowId\" = m.\"WorkflowId\" AND r.\"Kind\" = m.\"Kind\";\r\n\r\nUPDATE workflows w SET \"CurrentDefinitionStepId\" = m.step_id\r\nFROM legacy_task_step_map m\r\nWHERE w.\"Id\" = m.\"WorkflowId\" AND w.\"CurrentStep\" = m.\"Kind\"\r\n  AND w.\"CurrentDefinitionStepId\" IS NULL;\r\n\r\n-- LoopIteration intentionally stays NULL: all existing runs were non-loop executions.\r\nDO $$\r\nDECLARE duplicate record;\r\nBEGIN\r\n    SELECT \"WorkflowId\", \"DefinitionStepId\", \"LoopIteration\" INTO duplicate\r\n    FROM task_runs GROUP BY \"WorkflowId\", \"DefinitionStepId\", \"LoopIteration\"\r\n    HAVING count(*) > 1 LIMIT 1;\r\n    IF FOUND THEN\r\n        RAISE EXCEPTION 'Cannot normalize legacy workflow %: duplicate task-run key for step % (iteration %)',\r\n            duplicate.\"WorkflowId\", duplicate.\"DefinitionStepId\", duplicate.\"LoopIteration\";\r\n    END IF;\r\nEND $$;\r\n");

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
