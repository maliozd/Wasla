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

        builder.HasData(
            new City { Id = 1, CountryCode = "TR", Name = "Sakarya", PlateCode = "54", PhoneAreaCode = "264", SortOrder = 1, IsActive = true },
            new City { Id = 2, CountryCode = "TR", Name = "İstanbul", PlateCode = "34", PhoneAreaCode = "212", SortOrder = 2, IsActive = true },
            new City { Id = 3, CountryCode = "TR", Name = "Ankara", PlateCode = "06", PhoneAreaCode = "312", SortOrder = 3, IsActive = true },
            new City { Id = 4, CountryCode = "TR", Name = "İzmir", PlateCode = "35", PhoneAreaCode = "232", SortOrder = 4, IsActive = true });
    }
}
