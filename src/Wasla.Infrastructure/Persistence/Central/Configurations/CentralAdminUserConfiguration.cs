using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

public sealed class CentralAdminUserConfiguration : IEntityTypeConfiguration<CentralAdminUser>
{
    public void Configure(EntityTypeBuilder<CentralAdminUser> builder)
    {
        builder.ToTable("CentralAdminUsers");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Email).IsRequired().HasMaxLength(256);
        builder.Property(x => x.NormalizedEmail).IsRequired().HasMaxLength(256);
        builder.Property(x => x.PasswordHash).IsRequired().HasMaxLength(500);
        builder.Property(x => x.DisplayName).IsRequired().HasMaxLength(150);

        builder.Property(x => x.IsActive).HasDefaultValue(true);

        builder.HasIndex(x => x.NormalizedEmail)
            .IsUnique()
            .HasDatabaseName("IX_CentralAdminUsers_NormalizedEmail");
    }
}

