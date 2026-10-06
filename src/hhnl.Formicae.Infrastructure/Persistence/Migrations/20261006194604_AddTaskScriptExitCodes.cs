using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskScriptExitCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExitCode",
                table: "task_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExitCode",
                table: "task_run_attempts",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExitCode",
                table: "task_runs");

            migrationBuilder.DropColumn(
                name: "ExitCode",
                table: "task_run_attempts");
        }
    }
}
