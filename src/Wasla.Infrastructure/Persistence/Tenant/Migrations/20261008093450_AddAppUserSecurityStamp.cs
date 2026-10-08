using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Tenant.Migrations
{
    /// <inheritdoc />
    public partial class AddAppUserSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SecurityStamp",
                table: "AppUsers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every existing user gets their own stamp. An empty stamp is never accepted for a session, and sessions
            // issued before this migration carry no stamp, so every tenant user signs in again once.
            migrationBuilder.Sql("UPDATE [AppUsers] SET [SecurityStamp] = NEWID();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "AppUsers");
        }
    }
}
