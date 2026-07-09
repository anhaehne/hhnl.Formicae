using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowTriggerEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow_trigger_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowDefinitionVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerId = table.Column<string>(type: "text", nullable: false),
                    TriggerType = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    ExternalDeliveryId = table.Column<string>(type: "text", nullable: false),
                    EventName = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    PayloadSummaryJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_trigger_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_trigger_events_workflow_definition_versions_Workfl~",
                        column: x => x.WorkflowDefinitionVersionId,
                        principalTable: "workflow_definition_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workflow_trigger_events_workflow_definitions_WorkflowDefini~",
                        column: x => x.WorkflowDefinitionId,
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workflow_trigger_events_workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_trigger_events_CreatedAt",
                table: "workflow_trigger_events",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_trigger_events_ExternalDeliveryId",
                table: "workflow_trigger_events",
                column: "ExternalDeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_trigger_events_ExternalDeliveryId_TriggerId",
                table: "workflow_trigger_events",
                columns: new[] { "ExternalDeliveryId", "TriggerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_trigger_events_WorkflowDefinitionId",
                table: "workflow_trigger_events",
                column: "WorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_trigger_events_WorkflowDefinitionVersionId",
                table: "workflow_trigger_events",
                column: "WorkflowDefinitionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_trigger_events_WorkflowId",
                table: "workflow_trigger_events",
                column: "WorkflowId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_trigger_events");
        }
    }
}
