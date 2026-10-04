using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wasla.Infrastructure.Persistence.Tenant;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Tenant.Migrations;

[DbContext(typeof(TenantDbContext))]
[Migration("20260926140000_AddSetupGuidanceCompletedAt")]
public partial class AddSetupGuidanceCompletedAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "SetupGuidanceCompletedAtUtc",
            table: "TenantOperationalSettings",
            type: "datetime2",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SetupGuidanceCompletedAtUtc",
            table: "TenantOperationalSettings");
    }
}
