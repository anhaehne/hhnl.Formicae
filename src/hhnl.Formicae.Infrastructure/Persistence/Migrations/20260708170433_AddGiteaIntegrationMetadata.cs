using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGiteaIntegrationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessToken",
                table: "devops_integrations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServerUrl",
                table: "devops_integrations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessToken",
                table: "devops_integrations");

            migrationBuilder.DropColumn(
                name: "ServerUrl",
                table: "devops_integrations");
        }
    }
}
