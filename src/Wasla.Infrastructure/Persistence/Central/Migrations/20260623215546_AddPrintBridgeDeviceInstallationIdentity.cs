using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintBridgeDeviceInstallationIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "InstallationId",
                table: "PrintBridgeDevices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintBridgeDevices_TenantId_InstallationId_Active",
                table: "PrintBridgeDevices",
                columns: new[] { "TenantId", "InstallationId" },
                unique: true,
                filter: "[InstallationId] IS NOT NULL AND [RemovedAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrintBridgeDevices_TenantId_InstallationId_Active",
                table: "PrintBridgeDevices");

            migrationBuilder.DropColumn(
                name: "InstallationId",
                table: "PrintBridgeDevices");
        }
    }
}
