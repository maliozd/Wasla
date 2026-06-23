using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintBridgeDeviceRemovalState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RemovedAtUtc",
                table: "PrintBridgeDevices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintBridgeDevices_TenantId_RemovedAtUtc",
                table: "PrintBridgeDevices",
                columns: new[] { "TenantId", "RemovedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrintBridgeDevices_TenantId_RemovedAtUtc",
                table: "PrintBridgeDevices");

            migrationBuilder.DropColumn(
                name: "RemovedAtUtc",
                table: "PrintBridgeDevices");
        }
    }
}
