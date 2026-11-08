using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

/// <summary>Registration drafts (WAS-192). Lengths follow <see cref="PendingRegistrationConfiguration"/>.</summary>
public sealed class RegistrationDraftConfiguration : IEntityTypeConfiguration<RegistrationDraft>
{
    public const int PhoneE164MaxLength = 20;

    public void Configure(EntityTypeBuilder<RegistrationDraft> builder)
    {
        builder.ToTable("RegistrationDrafts");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Revision).IsConcurrencyToken();

        builder.Property(x => x.PlanCode).HasMaxLength(50);
        builder.Property(x => x.BillingPeriod).HasMaxLength(20);

        builder.Property(x => x.BusinessName).HasMaxLength(200);
        builder.Property(x => x.BusinessTypeCodes).HasMaxLength(1000).IsUnicode(false);
        builder.Property(x => x.BusinessPhoneType).HasConversion<int?>();
        builder.Property(x => x.BusinessPhone).HasMaxLength(50);
        builder.Property(x => x.BusinessEmail).HasMaxLength(256);
        builder.Property(x => x.Slug).HasMaxLength(100);
        builder.Property(x => x.Country).HasMaxLength(100);
        builder.Property(x => x.City).HasMaxLength(100);
        builder.Property(x => x.District).HasMaxLength(100);
        builder.Property(x => x.Neighborhood).HasMaxLength(150);
        builder.Property(x => x.StreetAddress).HasMaxLength(300);
        builder.Property(x => x.BuildingNumber).HasMaxLength(30);
        builder.Property(x => x.Floor).HasMaxLength(20);
        builder.Property(x => x.DoorNumber).HasMaxLength(20);
        builder.Property(x => x.AddressNote).HasMaxLength(300);
        builder.Property(x => x.PostalCode).HasMaxLength(20);
        builder.Property(x => x.LocationUrl).HasMaxLength(500);

        builder.Property(x => x.OwnerFullName).HasMaxLength(200);
        builder.Property(x => x.OwnerEmail).HasMaxLength(256);
        builder.Property(x => x.OwnerMobilePhone).HasMaxLength(PhoneE164MaxLength).IsUnicode(false);
        builder.Property(x => x.PasswordHash).HasMaxLength(500);
        builder.Property(x => x.PhoneVerifiedNumber).HasMaxLength(PhoneE164MaxLength).IsUnicode(false);

        builder.HasOne<PendingRegistration>()
            .WithMany()
            .HasForeignKey(x => x.PendingRegistrationId)
            .OnDelete(DeleteBehavior.SetNull);

        // A draft yields at most one registration, and a registration comes from at most one draft.
        builder.HasIndex(x => x.PendingRegistrationId)
            .IsUnique()
            .HasFilter("[PendingRegistrationId] IS NOT NULL")
            .HasDatabaseName("IX_RegistrationDrafts_PendingRegistrationId");
        builder.HasIndex(x => x.ExpiresAtUtc).HasDatabaseName("IX_RegistrationDrafts_ExpiresAtUtc");
    }
}

/// <summary>Per draft and number verification state (WAS-192), deleted with its draft.</summary>
public sealed class RegistrationPhoneVerificationConfiguration : IEntityTypeConfiguration<RegistrationPhoneVerification>
{
    public void Configure(EntityTypeBuilder<RegistrationPhoneVerification> builder)
    {
        builder.ToTable("RegistrationPhoneVerifications", table =>
        {
            table.HasCheckConstraint("CK_RegistrationPhoneVerifications_SendCount", "[SendCount] >= 0");
            table.HasCheckConstraint("CK_RegistrationPhoneVerifications_FailedAttempts", "[FailedAttempts] >= 0");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.PhoneNumber).IsRequired()
            .HasMaxLength(RegistrationDraftConfiguration.PhoneE164MaxLength).IsUnicode(false);
        builder.Property(x => x.ProtectedCode).HasMaxLength(1024).IsUnicode(false);
        builder.Property(x => x.LastSentAtUtc).IsConcurrencyToken();

        builder.HasOne<RegistrationDraft>()
            .WithMany()
            .HasForeignKey(x => x.DraftId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.DraftId, x.PhoneNumber })
            .IsUnique()
            .HasDatabaseName("IX_RegistrationPhoneVerifications_DraftId_PhoneNumber");
    }
}

/// <summary>Verification sends for the rolling number and global limits (WAS-192). No foreign key to the draft.</summary>
public sealed class RegistrationPhoneVerificationSendConfiguration : IEntityTypeConfiguration<RegistrationPhoneVerificationSend>
{
    public void Configure(EntityTypeBuilder<RegistrationPhoneVerificationSend> builder)
    {
        builder.ToTable("RegistrationPhoneVerificationSends");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.PhoneNumber).IsRequired()
            .HasMaxLength(RegistrationDraftConfiguration.PhoneE164MaxLength).IsUnicode(false);
        builder.Property(x => x.Outcome).HasConversion<int>();

        builder.HasIndex(x => new { x.PhoneNumber, x.SentAtUtc })
            .HasDatabaseName("IX_RegistrationPhoneVerificationSends_PhoneNumber_SentAtUtc");
        builder.HasIndex(x => x.SentAtUtc).HasDatabaseName("IX_RegistrationPhoneVerificationSends_SentAtUtc");
    }
}
