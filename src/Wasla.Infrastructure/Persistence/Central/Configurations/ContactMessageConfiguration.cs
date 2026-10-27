using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

/// <summary>Messages from the public contact form. Read by Central Admin newest first.</summary>
public sealed class ContactMessageConfiguration : IEntityTypeConfiguration<ContactMessage>
{
    public void Configure(EntityTypeBuilder<ContactMessage> builder)
    {
        builder.ToTable("ContactMessages");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.FullName).IsRequired().HasMaxLength(ContactMessageLimits.FullNameMaxLength);
        builder.Property(x => x.RestaurantName).IsRequired().HasMaxLength(ContactMessageLimits.RestaurantNameMaxLength);
        builder.Property(x => x.Email).IsRequired().HasMaxLength(ContactMessageLimits.EmailMaxLength);
        builder.Property(x => x.Phone).HasMaxLength(ContactMessageLimits.PhoneDigits).IsUnicode(false);
        builder.Property(x => x.Topic).HasConversion<int>();
        builder.Property(x => x.Message).IsRequired().HasMaxLength(ContactMessageLimits.MessageMaxLength);
        builder.Property(x => x.Culture).HasMaxLength(ContactMessageLimits.CultureMaxLength).IsUnicode(false);
        builder.Property(x => x.NotificationStatus).HasConversion<int>();

        builder.HasIndex(x => x.CreatedAtUtc).HasDatabaseName("IX_ContactMessages_CreatedAtUtc");
    }
}
