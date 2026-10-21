using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WAS-147: additive only. Two new tables; no existing table, column or row changes.
            migrationBuilder.CreateTable(
                name: "BillingSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PendingRegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    Environment = table.Column<int>(type: "int", nullable: false),
                    PlanCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BillingPeriod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExternalSubscriptionReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExternalSubscriberReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CurrentPeriodStartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CurrentPeriodEndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingSubscriptions", x => x.Id);
                    table.UniqueConstraint("AK_BillingSubscriptions_Id_Provider_Environment", x => new { x.Id, x.Provider, x.Environment });
                    table.CheckConstraint("CK_BillingSubscriptions_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_BillingSubscriptions_BillingPeriod", "[BillingPeriod] IN ('Monthly', 'Yearly')");
                    table.CheckConstraint("CK_BillingSubscriptions_CurrentPeriod", "[CurrentPeriodEndUtc] IS NULL OR ([CurrentPeriodStartUtc] IS NOT NULL AND [CurrentPeriodEndUtc] > [CurrentPeriodStartUtc])");
                    table.CheckConstraint("CK_BillingSubscriptions_Environment", "[Environment] IN (1, 2)");
                    table.CheckConstraint("CK_BillingSubscriptions_Provider", "[Provider] IN (1)");
                    table.CheckConstraint("CK_BillingSubscriptions_Status", "[Status] IN (2, 3, 4, 5, 6, 7)");
                    table.CheckConstraint("CK_BillingSubscriptions_VerifiedStateHasProviderReference", "[Status] NOT IN (2, 3) OR [ExternalSubscriptionReference] IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_BillingSubscriptions_PendingRegistrations_PendingRegistrationId",
                        column: x => x.PendingRegistrationId,
                        principalTable: "PendingRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillingSubscriptions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "BillingPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    Environment = table.Column<int>(type: "int", nullable: false),
                    ExternalPaymentReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PeriodEndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingPayments", x => x.Id);
                    table.CheckConstraint("CK_BillingPayments_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_BillingPayments_Period", "[PeriodEndUtc] > [PeriodStartUtc]");
                    table.ForeignKey(
                        name: "FK_BillingPayments_BillingSubscriptions",
                        columns: x => new { x.SubscriptionId, x.Provider, x.Environment },
                        principalTable: "BillingSubscriptions",
                        principalColumns: new[] { "Id", "Provider", "Environment" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingPayments_ProviderReference",
                table: "BillingPayments",
                columns: new[] { "Provider", "Environment", "ExternalPaymentReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingPayments_SubscriptionId_PeriodStartUtc",
                table: "BillingPayments",
                columns: new[] { "SubscriptionId", "PeriodStartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingPayments_SubscriptionId_Provider_Environment",
                table: "BillingPayments",
                columns: new[] { "SubscriptionId", "Provider", "Environment" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_PendingRegistrationId",
                table: "BillingSubscriptions",
                column: "PendingRegistrationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_ProviderReference",
                table: "BillingSubscriptions",
                columns: new[] { "Provider", "Environment", "ExternalSubscriptionReference" },
                unique: true,
                filter: "[ExternalSubscriptionReference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_TenantId",
                table: "BillingSubscriptions",
                column: "TenantId",
                unique: true,
                filter: "[TenantId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back would delete subscription and payment evidence. Refuse while any exists; an operator who
            // really means it exports and removes those rows first. Nothing is changed when this stops.
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM [BillingSubscriptions]) OR EXISTS (SELECT 1 FROM [BillingPayments])
                    THROW 50147, 'WAS-147: BillingSubscriptions or BillingPayments holds rows. Export and remove them before rolling back AddBillingSubscriptions.', 1;
                """);

            migrationBuilder.DropTable(
                name: "BillingPayments");

            migrationBuilder.DropTable(
                name: "BillingSubscriptions");
        }
    }
}
