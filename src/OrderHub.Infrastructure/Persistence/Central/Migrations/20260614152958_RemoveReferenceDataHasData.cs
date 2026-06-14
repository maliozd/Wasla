using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OrderHub.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class RemoveReferenceDataHasData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 5);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 6);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 7);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 8);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 9);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 11);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 12);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 13);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 14);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 15);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 16);

            migrationBuilder.DeleteData(
                table: "Districts",
                keyColumn: "Id",
                keyValue: 17);

            migrationBuilder.DeleteData(
                table: "Cities",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Cities",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "Cities",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "Cities",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.CreateIndex(
                name: "IX_Cities_CountryCode_PlateCode",
                table: "Cities",
                columns: new[] { "CountryCode", "PlateCode" },
                unique: true,
                filter: "[PlateCode] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Cities_CountryCode_PlateCode",
                table: "Cities");

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
        }
    }
}
