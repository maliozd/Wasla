using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintBridgeSetupDeviceSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "PrintBridgeDeviceId",
                table: "PrintBridgeSetupSessions",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "SetupMode",
                table: "PrintBridgeSetupSessions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE [PrintBridgeSetupSessions]
                SET [SetupMode] = N'ReconnectExisting'
                WHERE [SetupMode] IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "SetupMode",
                table: "PrintBridgeSetupSessions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM [PrintBridgeSetupSessions]
                WHERE [PrintBridgeDeviceId] IS NULL;
                """);

            migrationBuilder.DropColumn(
                name: "SetupMode",
                table: "PrintBridgeSetupSessions");

            migrationBuilder.AlterColumn<Guid>(
                name: "PrintBridgeDeviceId",
                table: "PrintBridgeSetupSessions",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
