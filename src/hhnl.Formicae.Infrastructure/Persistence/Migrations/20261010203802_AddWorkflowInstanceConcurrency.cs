using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowInstanceConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EnqueuedAt",
                table: "workflows",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasExecutionSlot",
                table: "workflows",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxActiveInstances",
                table: "workflow_definitions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "workflow_concurrency_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MaxActiveInstances = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_concurrency_settings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_concurrency_settings");

            migrationBuilder.DropColumn(
                name: "EnqueuedAt",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "HasExecutionSlot",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "MaxActiveInstances",
                table: "workflow_definitions");
        }
    }
}
