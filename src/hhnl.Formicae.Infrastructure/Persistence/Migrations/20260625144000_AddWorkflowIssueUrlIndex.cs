using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowIssueUrlIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_workflows_IssueUrl",
                table: "workflows",
                column: "IssueUrl",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflows_IssueUrl",
                table: "workflows");
        }
    }
}
