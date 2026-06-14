using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderHub.Domain.Entities.Central;

namespace OrderHub.Infrastructure.Persistence.Central.Configurations;

public class CityConfiguration : IEntityTypeConfiguration<City>
{
    public void Configure(EntityTypeBuilder<City> builder)
    {
        builder.ToTable("Cities");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CountryCode).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.PlateCode).HasMaxLength(10);
        builder.Property(x => x.PhoneAreaCode).HasMaxLength(20);
        builder.HasIndex(x => new { x.CountryCode, x.Name }).IsUnique();
        builder.HasIndex(x => new { x.CountryCode, x.PlateCode })
            .IsUnique()
            .HasFilter("[PlateCode] IS NOT NULL");
    }
}
