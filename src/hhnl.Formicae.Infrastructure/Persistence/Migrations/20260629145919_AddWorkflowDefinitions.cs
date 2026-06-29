using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DslSchemaVersion",
                table: "workflows",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowDefinitionId",
                table: "workflows",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowDefinitionVersionId",
                table: "workflows",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "workflow_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_definition_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DslSchemaVersion = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definition_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_definition_versions_workflow_definitions_WorkflowD~",
                        column: x => x.WorkflowDefinitionId,
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflows_WorkflowDefinitionId",
                table: "workflows",
                column: "WorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_workflows_WorkflowDefinitionVersionId",
                table: "workflows",
                column: "WorkflowDefinitionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_versions_IsDefault_IsEnabled",
                table: "workflow_definition_versions",
                columns: new[] { "IsDefault", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_versions_WorkflowDefinitionId_Version",
                table: "workflow_definition_versions",
                columns: new[] { "WorkflowDefinitionId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_definition_versions");

            migrationBuilder.DropTable(
                name: "workflow_definitions");

            migrationBuilder.DropIndex(
                name: "IX_workflows_WorkflowDefinitionId",
                table: "workflows");

            migrationBuilder.DropIndex(
                name: "IX_workflows_WorkflowDefinitionVersionId",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "DslSchemaVersion",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "WorkflowDefinitionId",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "WorkflowDefinitionVersionId",
                table: "workflows");
        }
    }
}
