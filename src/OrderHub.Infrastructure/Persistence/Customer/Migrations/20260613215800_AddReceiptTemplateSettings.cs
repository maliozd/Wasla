using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderHub.Infrastructure.Persistence.Customer.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptTemplateSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReceiptTemplateSettingsJson",
                table: "CustomerOperationalSettings",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReceiptTemplateSettingsJson",
                table: "CustomerOperationalSettings");
        }
    }
}
