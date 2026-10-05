using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralAdminSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SecurityStamp",
                table: "CentralAdminUsers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Give every existing admin a distinct stamp. Cookies issued before this migration carry no stamp claim
            // and are rejected regardless, so existing admins sign in once more after the release.
            migrationBuilder.Sql(
                """
                UPDATE [CentralAdminUsers]
                SET [SecurityStamp] = NEWID();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "CentralAdminUsers");
        }
    }
}
