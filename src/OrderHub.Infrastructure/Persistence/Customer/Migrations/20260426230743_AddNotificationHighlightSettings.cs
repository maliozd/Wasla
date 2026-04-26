using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderHub.Infrastructure.Persistence.Customer.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationHighlightSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NewOrderHighlightColor",
                table: "UserNotificationSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "yellow");

            migrationBuilder.AddColumn<string>(
                name: "NewOrderHighlightBehavior",
                table: "UserNotificationSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "fade");

            migrationBuilder.AddColumn<int>(
                name: "NewOrderHighlightDurationSeconds",
                table: "UserNotificationSettings",
                type: "int",
                nullable: false,
                defaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NewOrderHighlightBehavior",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "NewOrderHighlightColor",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "NewOrderHighlightDurationSeconds",
                table: "UserNotificationSettings");
        }
    }
}
