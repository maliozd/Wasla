using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderHub.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddAddressReferenceTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Neighborhood",
                table: "PendingRegistrations",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressNote",
                table: "PendingRegistrations",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuildingNumber",
                table: "PendingRegistrations",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BusinessEmail",
                table: "PendingRegistrations",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DoorNumber",
                table: "PendingRegistrations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Floor",
                table: "PendingRegistrations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationUrl",
                table: "PendingRegistrations",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NeighborhoodId",
                table: "PendingRegistrations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StreetAddress",
                table: "PendingRegistrations",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StreetId",
                table: "PendingRegistrations",
                type: "int",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE [PendingRegistrations]
                SET [StreetAddress] = [AddressLine1]
                WHERE [AddressLine1] IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE [PendingRegistrations]
                SET [AddressNote] = [AddressLine2]
                WHERE [AddressLine2] IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "AddressLine1",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "AddressLine2",
                table: "PendingRegistrations");

            migrationBuilder.CreateTable(
                name: "Countries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Countries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Neighborhoods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DistrictId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ExternalCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Neighborhoods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Neighborhoods_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalTable: "Districts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Streets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NeighborhoodId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StreetType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ExternalCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Streets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Streets_Neighborhoods_NeighborhoodId",
                        column: x => x.NeighborhoodId,
                        principalTable: "Neighborhoods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrations_NeighborhoodId",
                table: "PendingRegistrations",
                column: "NeighborhoodId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrations_StreetId",
                table: "PendingRegistrations",
                column: "StreetId");

            migrationBuilder.CreateIndex(
                name: "IX_Countries_Code",
                table: "Countries",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Neighborhoods_DistrictId_Name",
                table: "Neighborhoods",
                columns: new[] { "DistrictId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Streets_NeighborhoodId_Name_StreetType",
                table: "Streets",
                columns: new[] { "NeighborhoodId", "Name", "StreetType" },
                unique: true,
                filter: "[StreetType] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingRegistrations_Neighborhoods_NeighborhoodId",
                table: "PendingRegistrations",
                column: "NeighborhoodId",
                principalTable: "Neighborhoods",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PendingRegistrations_Streets_StreetId",
                table: "PendingRegistrations",
                column: "StreetId",
                principalTable: "Streets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingRegistrations_Neighborhoods_NeighborhoodId",
                table: "PendingRegistrations");

            migrationBuilder.DropForeignKey(
                name: "FK_PendingRegistrations_Streets_StreetId",
                table: "PendingRegistrations");

            migrationBuilder.DropTable(
                name: "Countries");

            migrationBuilder.DropTable(
                name: "Streets");

            migrationBuilder.DropTable(
                name: "Neighborhoods");

            migrationBuilder.DropIndex(
                name: "IX_PendingRegistrations_NeighborhoodId",
                table: "PendingRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_PendingRegistrations_StreetId",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "AddressNote",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "BuildingNumber",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "BusinessEmail",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "DoorNumber",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "Floor",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "LocationUrl",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "NeighborhoodId",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "StreetId",
                table: "PendingRegistrations");

            migrationBuilder.AddColumn<string>(
                name: "AddressLine1",
                table: "PendingRegistrations",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AddressLine2",
                table: "PendingRegistrations",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE [PendingRegistrations]
                SET [AddressLine1] = COALESCE([StreetAddress], '')
                WHERE [StreetAddress] IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE [PendingRegistrations]
                SET [AddressLine2] = [AddressNote]
                WHERE [AddressNote] IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "StreetAddress",
                table: "PendingRegistrations");

            migrationBuilder.AlterColumn<string>(
                name: "Neighborhood",
                table: "PendingRegistrations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(150)",
                oldMaxLength: 150,
                oldNullable: true);
        }
    }
}
