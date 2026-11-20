using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddPilotApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CityPlateCode",
                table: "ContactMessages",
                type: "varchar(2)",
                unicode: false,
                maxLength: 2,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContactMessages_Topic_CreatedAtUtc",
                table: "ContactMessages",
                columns: new[] { "Topic", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContactMessages_Topic_CreatedAtUtc",
                table: "ContactMessages");

            migrationBuilder.DropColumn(
                name: "CityPlateCode",
                table: "ContactMessages");
        }
    }
}
