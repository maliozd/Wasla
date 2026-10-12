using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantDatabaseNameUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WAS-93: two tenant rows naming one database must be resolved by an operator, never silently. Stop with
            // a readable error instead of a bare duplicate-key failure; the migration changes nothing in that case.
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM [Tenants] GROUP BY [DatabaseName] HAVING COUNT(*) > 1)
                    THROW 50093, 'WAS-93: several Tenants rows share one DatabaseName. Resolve them before applying AddTenantDatabaseNameUniqueIndex: SELECT [DatabaseName], COUNT(*) FROM [Tenants] GROUP BY [DatabaseName] HAVING COUNT(*) > 1.', 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_DatabaseName",
                table: "Tenants",
                column: "DatabaseName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tenants_DatabaseName",
                table: "Tenants");
        }
    }
}
