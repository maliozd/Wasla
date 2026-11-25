using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

/// <summary>
/// Messages from the public contact form and pilot applications from <c>/pilot</c> (WAS-217). Read by Central Admin
/// newest first, all together or one topic at a time.
/// </summary>
public sealed class ContactMessageConfiguration : IEntityTypeConfiguration<ContactMessage>
{
    public void Configure(EntityTypeBuilder<ContactMessage> builder)
    {
        // A pilot application always has a status and a last activity (WAS-222): the retention cleanup reads both. Other
        // topics are not constrained here; the cleanup never selects them, whatever these columns hold.
        builder.ToTable("ContactMessages", table => table.HasCheckConstraint(
            "CK_ContactMessages_PilotFields",
            "[Topic] <> 5 OR ([PilotStatus] IS NOT NULL AND [PilotLastActivityAtUtc] IS NOT NULL)"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.FullName).IsRequired().HasMaxLength(ContactMessageLimits.FullNameMaxLength);
        builder.Property(x => x.RestaurantName).IsRequired().HasMaxLength(ContactMessageLimits.RestaurantNameMaxLength);
        builder.Property(x => x.Email).IsRequired().HasMaxLength(ContactMessageLimits.EmailMaxLength);
        builder.Property(x => x.Phone).HasMaxLength(ContactMessageLimits.PhoneDigits).IsUnicode(false);
        builder.Property(x => x.Topic).HasConversion<int>();
        builder.Property(x => x.Message).IsRequired().HasMaxLength(ContactMessageLimits.MessageMaxLength);
        builder.Property(x => x.CityPlateCode).HasMaxLength(ContactMessageLimits.CityPlateCodeLength).IsUnicode(false);
        builder.Property(x => x.Culture).HasMaxLength(ContactMessageLimits.CultureMaxLength).IsUnicode(false);
        builder.Property(x => x.NotificationStatus).HasConversion<int>();
        builder.Property(x => x.PilotStatus).HasConversion<int?>();

        builder.HasIndex(x => x.CreatedAtUtc).HasDatabaseName("IX_ContactMessages_CreatedAtUtc");

        // The Central Admin list filtered to one topic (pilot applications or contact messages), newest first.
        builder.HasIndex(x => new { x.Topic, x.CreatedAtUtc }).HasDatabaseName("IX_ContactMessages_Topic_CreatedAtUtc");

        // The retention cleanup (WAS-222): pilot applications by last activity, oldest first, read in batches. It covers
        // every column the batch read filters on or returns, so the read never looks a row up in the clustered index: a
        // lookup holds this index's key while it waits for the row, and a concurrent delete locks the row first and then
        // this key, which deadlocked two overlapping runs (error 1205, the shape of WAS-203/WAS-205).
        builder.HasIndex(x => new { x.Topic, x.PilotLastActivityAtUtc, x.Id })
            .HasDatabaseName("IX_ContactMessages_Topic_PilotLastActivityAtUtc")
            .IncludeProperties(x => new { x.PilotStatus, x.NotificationClaimId, x.NotificationAttemptedAtUtc, x.CreatedAtUtc });
    }
}
