using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubAppPrivateKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GitHubAppPrivateKey",
                table: "devops_integrations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitHubAppSlug",
                table: "devops_integrations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitHubAppPrivateKey",
                table: "devops_integrations");

            migrationBuilder.DropColumn(
                name: "GitHubAppSlug",
                table: "devops_integrations");
        }
    }
}
