using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderHub.Domain.Entities.Central;

namespace OrderHub.Infrastructure.Persistence.Central.Configurations;

public class StreetConfiguration : IEntityTypeConfiguration<Street>
{
    public void Configure(EntityTypeBuilder<Street> builder)
    {
        builder.ToTable("Streets");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.StreetType).HasMaxLength(50);
        builder.Property(x => x.ExternalCode).HasMaxLength(50);
        builder.HasIndex(x => new { x.NeighborhoodId, x.Name, x.StreetType })
            .IsUnique()
            .HasFilter("[StreetType] IS NOT NULL");

        builder.HasOne(x => x.Neighborhood)
            .WithMany(x => x.Streets)
            .HasForeignKey(x => x.NeighborhoodId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
