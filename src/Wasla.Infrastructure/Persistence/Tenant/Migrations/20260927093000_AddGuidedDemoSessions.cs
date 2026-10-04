using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wasla.Infrastructure.Persistence.Tenant;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Tenant.Migrations;

[DbContext(typeof(TenantDbContext))]
[Migration("20260927093000_AddGuidedDemoSessions")]
public partial class AddGuidedDemoSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GuidedDemoSessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ScenarioCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                CustomerNameKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                NoteKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                ItemsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GuidedDemoSessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_GuidedDemoSessions_AppUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AppUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_GuidedDemoSessions_UserId_CompletedAtUtc",
            table: "GuidedDemoSessions",
            columns: new[] { "UserId", "CompletedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "GuidedDemoSessions");
    }
}
