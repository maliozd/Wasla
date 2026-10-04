using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wasla.Infrastructure.Persistence.Tenant;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Tenant.Migrations;

[DbContext(typeof(TenantDbContext))]
[Migration("20260926183000_AddUserProductTourCompletions")]
public partial class AddUserProductTourCompletions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserProductTourCompletions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TourKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserProductTourCompletions", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserProductTourCompletions_AppUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AppUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserProductTourCompletions_UserId_TourKey",
            table: "UserProductTourCompletions",
            columns: new[] { "UserId", "TourKey" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UserProductTourCompletions");
    }
}
