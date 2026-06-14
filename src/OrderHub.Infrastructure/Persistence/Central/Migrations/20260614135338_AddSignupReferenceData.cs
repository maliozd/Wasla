using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OrderHub.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddSignupReferenceData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "BusinessType",
                table: "PendingRegistrations",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100);

            migrationBuilder.AddColumn<int>(
                name: "BusinessPhoneType",
                table: "PendingRegistrations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CityId",
                table: "PendingRegistrations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DistrictId",
                table: "PendingRegistrations",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BusinessTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Cities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CountryCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PlateCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    PhoneAreaCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PendingRegistrationBusinessTypes",
                columns: table => new
                {
                    PendingRegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessTypeId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingRegistrationBusinessTypes", x => new { x.PendingRegistrationId, x.BusinessTypeId });
                    table.ForeignKey(
                        name: "FK_PendingRegistrationBusinessTypes_BusinessTypes_BusinessTypeId",
                        column: x => x.BusinessTypeId,
                        principalTable: "BusinessTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PendingRegistrationBusinessTypes_PendingRegistrations_PendingRegistrationId",
                        column: x => x.PendingRegistrationId,
                        principalTable: "PendingRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Districts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CityId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Districts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Districts_Cities_CityId",
                        column: x => x.CityId,
                        principalTable: "Cities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "BusinessTypes",
                columns: new[] { "Id", "Code", "DisplayName", "IsActive", "SortOrder" },
                values: new object[,]
                {
                    { 1, "restaurant", "Restoran", true, 1 },
                    { 2, "fast-food", "Fast food", true, 2 },
                    { 3, "cafe", "Kafe", true, 3 },
                    { 4, "dessert", "Tatlıcı", true, 4 },
                    { 5, "bakery", "Pastane", true, 5 },
                    { 6, "pide-lahmacun", "Pide / Lahmacun", true, 6 },
                    { 7, "doner", "Döner", true, 7 },
                    { 8, "pizza", "Pizza", true, 8 },
                    { 9, "burger", "Burger", true, 9 },
                    { 10, "sushi", "Sushi", true, 10 },
                    { 11, "home-cooking", "Ev yemekleri", true, 11 },
                    { 12, "other", "Diğer", true, 12 }
                });

            migrationBuilder.InsertData(
                table: "Cities",
                columns: new[] { "Id", "CountryCode", "IsActive", "Name", "PhoneAreaCode", "PlateCode", "SortOrder" },
                values: new object[,]
                {
                    { 1, "TR", true, "Sakarya", "264", "54", 1 },
                    { 2, "TR", true, "İstanbul", "212", "34", 2 },
                    { 3, "TR", true, "Ankara", "312", "06", 3 },
                    { 4, "TR", true, "İzmir", "232", "35", 4 }
                });

            migrationBuilder.InsertData(
                table: "Districts",
                columns: new[] { "Id", "CityId", "IsActive", "Name", "SortOrder" },
                values: new object[,]
                {
                    { 1, 1, true, "Akyazı", 1 },
                    { 2, 1, true, "Adapazarı", 2 },
                    { 3, 1, true, "Serdivan", 3 },
                    { 4, 1, true, "Erenler", 4 },
                    { 5, 1, true, "Hendek", 5 },
                    { 6, 1, true, "Karasu", 6 },
                    { 7, 2, true, "Kadıköy", 1 },
                    { 8, 2, true, "Üsküdar", 2 },
                    { 9, 2, true, "Beşiktaş", 3 },
                    { 10, 2, true, "Fatih", 4 },
                    { 11, 2, true, "Şişli", 5 },
                    { 12, 3, true, "Çankaya", 1 },
                    { 13, 3, true, "Keçiören", 2 },
                    { 14, 3, true, "Yenimahalle", 3 },
                    { 15, 4, true, "Konak", 1 },
                    { 16, 4, true, "Bornova", 2 },
                    { 17, 4, true, "Karşıyaka", 3 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrations_CityId",
                table: "PendingRegistrations",
                column: "CityId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrations_DistrictId",
                table: "PendingRegistrations",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessTypes_Code",
                table: "BusinessTypes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cities_CountryCode_Name",
                table: "Cities",
                columns: new[] { "CountryCode", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Districts_CityId_Name",
                table: "Districts",
                columns: new[] { "CityId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrationBusinessTypes_BusinessTypeId",
                table: "PendingRegistrationBusinessTypes",
                column: "BusinessTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingRegistrations_Cities_CityId",
                table: "PendingRegistrations",
                column: "CityId",
                principalTable: "Cities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PendingRegistrations_Districts_DistrictId",
                table: "PendingRegistrations",
                column: "DistrictId",
                principalTable: "Districts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingRegistrations_Cities_CityId",
                table: "PendingRegistrations");

            migrationBuilder.DropForeignKey(
                name: "FK_PendingRegistrations_Districts_DistrictId",
                table: "PendingRegistrations");

            migrationBuilder.DropTable(
                name: "Districts");

            migrationBuilder.DropTable(
                name: "PendingRegistrationBusinessTypes");

            migrationBuilder.DropTable(
                name: "Cities");

            migrationBuilder.DropTable(
                name: "BusinessTypes");

            migrationBuilder.DropIndex(
                name: "IX_PendingRegistrations_CityId",
                table: "PendingRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_PendingRegistrations_DistrictId",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "BusinessPhoneType",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "CityId",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "DistrictId",
                table: "PendingRegistrations");

            migrationBuilder.AlterColumn<string>(
                name: "BusinessType",
                table: "PendingRegistrations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(300)",
                oldMaxLength: 300,
                oldNullable: true);
        }
    }
}
