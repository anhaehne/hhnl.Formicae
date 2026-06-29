using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(FormicaeDbContext))]
    [Migration("20260629100000_AddAgentAuthSettings")]
    public partial class AddAgentAuthSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "AgentKind", table: "ai_settings", type: "text", nullable: false, defaultValue: "OpenHands");
            migrationBuilder.AddColumn<string>(name: "AcpProvider", table: "ai_settings", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "AcpCommand", table: "ai_settings", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "LlmApiKey", table: "ai_settings", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "ApiKeyEnvironmentVariable", table: "ai_settings", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "SubscriptionCredentialJson", table: "ai_settings", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "SubscriptionCredentialFileName", table: "ai_settings", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "SubscriptionCredentialMountPath", table: "ai_settings", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "CodexAuthJson", table: "ai_settings", type: "text", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "AgentKind", table: "ai_settings");
            migrationBuilder.DropColumn(name: "AcpProvider", table: "ai_settings");
            migrationBuilder.DropColumn(name: "AcpCommand", table: "ai_settings");
            migrationBuilder.DropColumn(name: "LlmApiKey", table: "ai_settings");
            migrationBuilder.DropColumn(name: "ApiKeyEnvironmentVariable", table: "ai_settings");
            migrationBuilder.DropColumn(name: "SubscriptionCredentialJson", table: "ai_settings");
            migrationBuilder.DropColumn(name: "SubscriptionCredentialFileName", table: "ai_settings");
            migrationBuilder.DropColumn(name: "SubscriptionCredentialMountPath", table: "ai_settings");
            migrationBuilder.DropColumn(name: "CodexAuthJson", table: "ai_settings");
        }
    }
}