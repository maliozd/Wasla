using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintBridgeSetupTransfer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourcePrintBridgeDeviceId",
                table: "PrintBridgeSetupSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTokenHash",
                table: "PrintBridgeSetupSessions",
                type: "nvarchar(88)",
                maxLength: 88,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourcePrintBridgeDeviceId",
                table: "PrintBridgeSetupSessions");

            migrationBuilder.DropColumn(
                name: "SourceTokenHash",
                table: "PrintBridgeSetupSessions");
        }
    }
}
