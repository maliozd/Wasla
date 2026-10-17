using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Tenant.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderTimeSemantics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtPlatform",
                table: "Orders",
                type: "datetime2",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            // A creation time before 2000 is a missing provider value read as zero (Trendyol GO stored 1970-01-01), not
            // an order time: it becomes unknown, so the order time falls back to the import time, as for new orders.
            migrationBuilder.Sql("UPDATE [Orders] SET [CreatedAtPlatform] = NULL WHERE [CreatedAtPlatform] < '2000-01-01';");

            migrationBuilder.AddColumn<DateTime>(
                name: "PlatformModifiedAt",
                table: "Orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OrderedAt",
                table: "Orders",
                type: "datetime2",
                nullable: false,
                computedColumnSql: "COALESCE(CreatedAtPlatform, ReceivedAt)",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_OrderedAt",
                table: "Orders",
                column: "OrderedAt",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_OrderedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OrderedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PlatformModifiedAt",
                table: "Orders");

            // The old column was required: an order without a provider time gets its import time back, which is what the
            // old code would have stored for it at best.
            migrationBuilder.Sql("UPDATE [Orders] SET [CreatedAtPlatform] = [ReceivedAt] WHERE [CreatedAtPlatform] IS NULL;");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtPlatform",
                table: "Orders",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);
        }
    }
}
