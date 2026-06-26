using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace hhnl.Formicae.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(FormicaeDbContext))]
    [Migration("20260626143000_AddAuthUsers")]
    public partial class AddAuthUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GitHubUserId = table.Column<string>(type: "text", nullable: false),
                    GitHubLogin = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    InviteCodeHash = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_auth_users_GitHubUserId",
                table: "auth_users",
                column: "GitHubUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_auth_users_GitHubLogin",
                table: "auth_users",
                column: "GitHubLogin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_users");
        }
    }
}
