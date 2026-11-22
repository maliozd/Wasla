using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddPilotApplicationRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PilotLastActivityAtUtc",
                table: "ContactMessages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PilotStatus",
                table: "ContactMessages",
                type: "int",
                nullable: true);

            // Pilot applications stored before WAS-222 start as New, last active when they were received, so the
            // retention rule applies to them from their submission. Contact form messages (other topics) stay null.
            migrationBuilder.Sql(
                "UPDATE [ContactMessages] SET [PilotStatus] = 0, [PilotLastActivityAtUtc] = [CreatedAtUtc] " +
                "WHERE [Topic] = 5 AND [PilotLastActivityAtUtc] IS NULL;");

            migrationBuilder.CreateTable(
                name: "DataDestructionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<int>(type: "int", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: false),
                    Result = table.Column<int>(type: "int", nullable: false),
                    Actor = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetainUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataDestructionRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContactMessages_Topic_PilotLastActivityAtUtc",
                table: "ContactMessages",
                columns: new[] { "Topic", "PilotLastActivityAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DataDestructionRecords_RetainUntilUtc",
                table: "DataDestructionRecords",
                columns: new[] { "RetainUntilUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DataDestructionRecords_SubjectId",
                table: "DataDestructionRecords",
                column: "SubjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataDestructionRecords");

            migrationBuilder.DropIndex(
                name: "IX_ContactMessages_Topic_PilotLastActivityAtUtc",
                table: "ContactMessages");

            migrationBuilder.DropColumn(
                name: "PilotLastActivityAtUtc",
                table: "ContactMessages");

            migrationBuilder.DropColumn(
                name: "PilotStatus",
                table: "ContactMessages");
        }
    }
}
