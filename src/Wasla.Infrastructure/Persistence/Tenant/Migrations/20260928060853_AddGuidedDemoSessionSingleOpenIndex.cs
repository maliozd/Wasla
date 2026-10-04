using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Tenant.Migrations
{
    /// <inheritdoc />
    public partial class AddGuidedDemoSessionSingleOpenIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing duplicates would block the unique index: keep each user's newest open session.
            migrationBuilder.Sql("""
                WITH [Ranked] AS (
                    SELECT [Id], ROW_NUMBER() OVER (
                        PARTITION BY [UserId]
                        ORDER BY [ReceivedAtUtc] DESC, [CreatedAt] DESC, [Id] DESC) AS [Position]
                    FROM [GuidedDemoSessions]
                    WHERE [CompletedAtUtc] IS NULL
                )
                UPDATE [Session]
                SET [CompletedAtUtc] = SYSUTCDATETIME(), [UpdatedAt] = SYSUTCDATETIME()
                FROM [GuidedDemoSessions] AS [Session]
                INNER JOIN [Ranked] ON [Ranked].[Id] = [Session].[Id]
                WHERE [Ranked].[Position] > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_GuidedDemoSessions_UserId_Open",
                table: "GuidedDemoSessions",
                column: "UserId",
                unique: true,
                filter: "[CompletedAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GuidedDemoSessions_UserId_Open",
                table: "GuidedDemoSessions");
        }
    }
}
