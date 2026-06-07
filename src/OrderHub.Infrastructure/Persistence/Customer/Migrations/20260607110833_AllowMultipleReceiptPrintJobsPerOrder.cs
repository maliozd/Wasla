using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderHub.Infrastructure.Persistence.Customer.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultipleReceiptPrintJobsPerOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_OrderId_Type",
                table: "PrintJobs");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_OrderId_Type",
                table: "PrintJobs",
                columns: new[] { "OrderId", "Type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_OrderId_Type",
                table: "PrintJobs");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_OrderId_Type",
                table: "PrintJobs",
                columns: new[] { "OrderId", "Type" },
                unique: true);
        }
    }
}
