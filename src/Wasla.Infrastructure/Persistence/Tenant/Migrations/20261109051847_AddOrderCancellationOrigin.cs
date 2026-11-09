using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Tenant.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCancellationOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CancellationOrigin",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CancellationReasonCode",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CancellationReasonPlatform",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_CancellationMetadata",
                table: "Orders",
                sql: "(CancellationOrigin IS NULL OR InternalStatus = 6) AND ((CancellationReasonCode IS NULL AND CancellationReasonPlatform IS NULL) OR (CancellationOrigin IS NOT NULL AND CancellationReasonCode IS NOT NULL AND CancellationReasonPlatform IS NOT NULL AND CancellationReasonPlatform = Platform))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_CancellationMetadata",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CancellationOrigin",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CancellationReasonCode",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CancellationReasonPlatform",
                table: "Orders");
        }
    }
}
