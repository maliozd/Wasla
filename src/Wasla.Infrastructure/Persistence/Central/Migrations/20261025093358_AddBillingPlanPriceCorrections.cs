using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <summary>
    /// GitHub #64: a revision on every plan price (existing rows start at 1) and the append-only audit of corrections
    /// of unreferenced prices. Additive; no existing row changes except gaining the revision.
    /// </summary>
    public partial class AddBillingPlanPriceCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "BillingPlanPrices",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "BillingPlanPriceCorrections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanPriceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    CorrectedByCentralAdminUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PreviousAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PreviousCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    PreviousVatIncluded = table.Column<bool>(type: "bit", nullable: false),
                    PreviousEffectiveFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NewAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NewCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    NewVatIncluded = table.Column<bool>(type: "bit", nullable: false),
                    NewEffectiveFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingPlanPriceCorrections", x => x.Id);
                    table.CheckConstraint("CK_BillingPlanPriceCorrections_NewAmount", "[NewAmount] > 0");
                    table.CheckConstraint("CK_BillingPlanPriceCorrections_PreviousAmount", "[PreviousAmount] > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingPlanPriceCorrections_PlanPriceId_CorrectedAtUtc",
                table: "BillingPlanPriceCorrections",
                columns: new[] { "PlanPriceId", "CorrectedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back would delete the only record of earlier plan price values. Refuse while any correction is
            // recorded; an operator who really means it exports and removes those rows first. Nothing changes when this stops.
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM [BillingPlanPriceCorrections])
                    THROW 50064, 'GitHub #64: BillingPlanPriceCorrections holds rows. Export and remove them before rolling back AddBillingPlanPriceCorrections.', 1;
                """);

            migrationBuilder.DropTable(
                name: "BillingPlanPriceCorrections");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "BillingPlanPrices");
        }
    }
}
