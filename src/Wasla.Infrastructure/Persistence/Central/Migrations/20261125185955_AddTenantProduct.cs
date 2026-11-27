using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Product",
                table: "Tenants",
                type: "int",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Product",
                table: "Tenants");
        }
    }
}
