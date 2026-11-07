using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wasla.Infrastructure.Persistence.Central.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OwnerPhoneVerifiedAtUtc",
                table: "PendingRegistrations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RegistrationDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    PlanCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BillingPeriod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PlanSavedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BusinessName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BusinessTypeCodes = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    BusinessPhoneType = table.Column<int>(type: "int", nullable: true),
                    BusinessPhone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BusinessEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Slug = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CityId = table.Column<int>(type: "int", nullable: true),
                    DistrictId = table.Column<int>(type: "int", nullable: true),
                    NeighborhoodId = table.Column<int>(type: "int", nullable: true),
                    StreetId = table.Column<int>(type: "int", nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    District = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Neighborhood = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    StreetAddress = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    BuildingNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Floor = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    DoorNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AddressNote = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LocationUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RestaurantSavedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OwnerFullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OwnerEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OwnerMobilePhone = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OwnerSavedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PhoneVerifiedNumber = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    PhoneVerifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PendingRegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegistrationDrafts_PendingRegistrations_PendingRegistrationId",
                        column: x => x.PendingRegistrationId,
                        principalTable: "PendingRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationPhoneVerificationSends",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhoneNumber = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationPhoneVerificationSends", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationPhoneVerifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhoneNumber = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProtectedCode = table.Column<string>(type: "varchar(1024)", unicode: false, maxLength: 1024, nullable: true),
                    CodeIssuedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CodeExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SendCount = table.Column<int>(type: "int", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    LockedUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationPhoneVerifications", x => x.Id);
                    table.CheckConstraint("CK_RegistrationPhoneVerifications_FailedAttempts", "[FailedAttempts] >= 0");
                    table.CheckConstraint("CK_RegistrationPhoneVerifications_SendCount", "[SendCount] >= 0");
                    table.ForeignKey(
                        name: "FK_RegistrationPhoneVerifications_RegistrationDrafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "RegistrationDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PendingRegistrations_OwnerPhoneVerified",
                table: "PendingRegistrations",
                sql: "[OwnerPhoneVerifiedAtUtc] IS NULL OR [OwnerPhone] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationDrafts_ExpiresAtUtc",
                table: "RegistrationDrafts",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationDrafts_PendingRegistrationId",
                table: "RegistrationDrafts",
                column: "PendingRegistrationId",
                unique: true,
                filter: "[PendingRegistrationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationPhoneVerifications_DraftId_PhoneNumber",
                table: "RegistrationPhoneVerifications",
                columns: new[] { "DraftId", "PhoneNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationPhoneVerificationSends_PhoneNumber_SentAtUtc",
                table: "RegistrationPhoneVerificationSends",
                columns: new[] { "PhoneNumber", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationPhoneVerificationSends_SentAtUtc",
                table: "RegistrationPhoneVerificationSends",
                column: "SentAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back would delete the evidence that registration owners verified their phone (WAS-192). Refuse while
            // any registration holds it; drafts and verification rows are short-lived and are dropped. Nothing is changed
            // when this stops.
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM [PendingRegistrations] WHERE [OwnerPhoneVerifiedAtUtc] IS NOT NULL)
                    THROW 50192, 'WAS-192: PendingRegistrations holds owner phone verification times. Export them and clear OwnerPhoneVerifiedAtUtc before rolling back AddRegistrationDrafts.', 1;
                """);

            migrationBuilder.DropTable(
                name: "RegistrationPhoneVerifications");

            migrationBuilder.DropTable(
                name: "RegistrationPhoneVerificationSends");

            migrationBuilder.DropTable(
                name: "RegistrationDrafts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PendingRegistrations_OwnerPhoneVerified",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "OwnerPhoneVerifiedAtUtc",
                table: "PendingRegistrations");
        }
    }
}
