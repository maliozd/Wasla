using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Tenant.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantOperationalMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0 is TenantOperationalMode.Live: every existing tenant keeps operating. A tenant without a settings row
            // also reads as Live. Only provisioning (and the Development tenant reset) writes Setup (1).
            migrationBuilder.AddColumn<int>(
                name: "OperationalMode",
                table: "TenantOperationalSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OperationalMode",
                table: "TenantOperationalSettings");
        }
    }
}
