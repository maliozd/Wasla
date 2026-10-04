using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Tenant.Migrations
{
    /// <inheritdoc />
    public partial class EnforceOnePlatformConnectionPerPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlatformConnections_Platform_StoreId",
                table: "PlatformConnections");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConnections_Platform",
                table: "PlatformConnections",
                column: "Platform",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlatformConnections_Platform",
                table: "PlatformConnections");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConnections_Platform_StoreId",
                table: "PlatformConnections",
                columns: new[] { "Platform", "StoreId" },
                unique: true);
        }
    }
}
