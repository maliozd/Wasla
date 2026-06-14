using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderHub.Domain.Entities.Central;

namespace OrderHub.Infrastructure.Persistence.Central.Configurations;

public class DistrictConfiguration : IEntityTypeConfiguration<District>
{
    public void Configure(EntityTypeBuilder<District> builder)
    {
        builder.ToTable("Districts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.HasIndex(x => new { x.CityId, x.Name }).IsUnique();

        builder.HasOne(x => x.City)
            .WithMany(x => x.Districts)
            .HasForeignKey(x => x.CityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasData(
            new District { Id = 1, CityId = 1, Name = "Akyazı", SortOrder = 1, IsActive = true },
            new District { Id = 2, CityId = 1, Name = "Adapazarı", SortOrder = 2, IsActive = true },
            new District { Id = 3, CityId = 1, Name = "Serdivan", SortOrder = 3, IsActive = true },
            new District { Id = 4, CityId = 1, Name = "Erenler", SortOrder = 4, IsActive = true },
            new District { Id = 5, CityId = 1, Name = "Hendek", SortOrder = 5, IsActive = true },
            new District { Id = 6, CityId = 1, Name = "Karasu", SortOrder = 6, IsActive = true },
            new District { Id = 7, CityId = 2, Name = "Kadıköy", SortOrder = 1, IsActive = true },
            new District { Id = 8, CityId = 2, Name = "Üsküdar", SortOrder = 2, IsActive = true },
            new District { Id = 9, CityId = 2, Name = "Beşiktaş", SortOrder = 3, IsActive = true },
            new District { Id = 10, CityId = 2, Name = "Fatih", SortOrder = 4, IsActive = true },
            new District { Id = 11, CityId = 2, Name = "Şişli", SortOrder = 5, IsActive = true },
            new District { Id = 12, CityId = 3, Name = "Çankaya", SortOrder = 1, IsActive = true },
            new District { Id = 13, CityId = 3, Name = "Keçiören", SortOrder = 2, IsActive = true },
            new District { Id = 14, CityId = 3, Name = "Yenimahalle", SortOrder = 3, IsActive = true },
            new District { Id = 15, CityId = 4, Name = "Konak", SortOrder = 1, IsActive = true },
            new District { Id = 16, CityId = 4, Name = "Bornova", SortOrder = 2, IsActive = true },
            new District { Id = 17, CityId = 4, Name = "Karşıyaka", SortOrder = 3, IsActive = true });
    }
}
