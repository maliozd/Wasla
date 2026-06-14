using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

public class BusinessTypeConfiguration : IEntityTypeConfiguration<BusinessType>
{
    public void Configure(EntityTypeBuilder<BusinessType> builder)
    {
        builder.ToTable("BusinessTypes");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).IsRequired().HasMaxLength(50);
        builder.Property(x => x.DisplayName).IsRequired().HasMaxLength(100);
        builder.HasIndex(x => x.Code).IsUnique();

        builder.HasData(
            new BusinessType { Id = 1, Code = "restaurant", DisplayName = "Restoran", SortOrder = 1, IsActive = true },
            new BusinessType { Id = 2, Code = "fast-food", DisplayName = "Fast food", SortOrder = 2, IsActive = true },
            new BusinessType { Id = 3, Code = "cafe", DisplayName = "Kafe", SortOrder = 3, IsActive = true },
            new BusinessType { Id = 4, Code = "dessert", DisplayName = "Tatlıcı", SortOrder = 4, IsActive = true },
            new BusinessType { Id = 5, Code = "bakery", DisplayName = "Pastane", SortOrder = 5, IsActive = true },
            new BusinessType { Id = 6, Code = "pide-lahmacun", DisplayName = "Pide / Lahmacun", SortOrder = 6, IsActive = true },
            new BusinessType { Id = 7, Code = "doner", DisplayName = "Döner", SortOrder = 7, IsActive = true },
            new BusinessType { Id = 8, Code = "pizza", DisplayName = "Pizza", SortOrder = 8, IsActive = true },
            new BusinessType { Id = 9, Code = "burger", DisplayName = "Burger", SortOrder = 9, IsActive = true },
            new BusinessType { Id = 10, Code = "sushi", DisplayName = "Sushi", SortOrder = 10, IsActive = true },
            new BusinessType { Id = 11, Code = "home-cooking", DisplayName = "Ev yemekleri", SortOrder = 11, IsActive = true },
            new BusinessType { Id = 12, Code = "other", DisplayName = "Diğer", SortOrder = 12, IsActive = true });
    }
}
