using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Tenant.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintJobInstallationOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LockedByInstallationId",
                table: "PrintJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_Status_LockedByInstallationId",
                table: "PrintJobs",
                columns: new[] { "Status", "LockedByInstallationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_Status_LockedByInstallationId",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "LockedByInstallationId",
                table: "PrintJobs");
        }
    }
}
