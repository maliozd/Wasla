using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintBridgeSetupSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrintBridgeSetupSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrintBridgeDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(88)", maxLength: 88, nullable: false),
                    CompletionCredentialHash = table.Column<string>(type: "nvarchar(88)", maxLength: 88, nullable: true),
                    ServerUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExchangedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConnectionVerified = table.Column<bool>(type: "bit", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintBridgeSetupSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrintBridgeSetupSessions_PrintBridgeDevices_PrintBridgeDeviceId",
                        column: x => x.PrintBridgeDeviceId,
                        principalTable: "PrintBridgeDevices",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PrintBridgeSetupSessions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrintBridgeSetupSessions_CodeHash",
                table: "PrintBridgeSetupSessions",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintBridgeSetupSessions_ExpiresAtUtc",
                table: "PrintBridgeSetupSessions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PrintBridgeSetupSessions_PrintBridgeDeviceId",
                table: "PrintBridgeSetupSessions",
                column: "PrintBridgeDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintBridgeSetupSessions_TenantId",
                table: "PrintBridgeSetupSessions",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrintBridgeSetupSessions");
        }
    }
}
