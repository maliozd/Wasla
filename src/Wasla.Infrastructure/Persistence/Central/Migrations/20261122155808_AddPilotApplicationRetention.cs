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
            // retention rule applies to them from their submission. Contact form messages (other topics) stay null. This
            // runs before CK_ContactMessages_PilotFields is added, which every pilot row then satisfies.
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
                columns: new[] { "Topic", "PilotLastActivityAtUtc", "Id" })
                .Annotation("SqlServer:Include", new[] { "PilotStatus", "NotificationClaimId", "NotificationAttemptedAtUtc", "CreatedAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContactMessages_PilotFields",
                table: "ContactMessages",
                sql: "[Topic] <> 5 OR ([PilotStatus] IS NOT NULL AND [PilotLastActivityAtUtc] IS NOT NULL)");

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
            // Rolling back would delete destruction records, which are kept for three years as evidence of deletions, and
            // the pilot status and last activity that Up cannot rebuild: applying it again would restart an application's
            // retention period from its submission. Refuse while either exists. A pilot application still New and last
            // active when received holds nothing Up does not rebuild. Nothing is changed when this stops.
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM [DataDestructionRecords])
                    THROW 50222, 'WAS-222: DataDestructionRecords holds destruction records, which are kept for three years. Export them and roll forward instead of rolling back AddPilotApplicationRetention.', 1;
                IF EXISTS (SELECT 1 FROM [ContactMessages] WHERE [Topic] = 5 AND ([PilotStatus] IS NULL OR [PilotStatus] <> 0 OR [PilotLastActivityAtUtc] IS NULL OR [PilotLastActivityAtUtc] <> [CreatedAtUtc]))
                    THROW 50222, 'WAS-222: pilot applications hold a status or last activity that rolling back AddPilotApplicationRetention would lose and applying it again would reset. Roll forward instead.', 1;
                """);

            migrationBuilder.DropTable(
                name: "DataDestructionRecords");

            migrationBuilder.DropIndex(
                name: "IX_ContactMessages_Topic_PilotLastActivityAtUtc",
                table: "ContactMessages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContactMessages_PilotFields",
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
