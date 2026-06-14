using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

public class PendingRegistrationBusinessTypeConfiguration : IEntityTypeConfiguration<PendingRegistrationBusinessType>
{
    public void Configure(EntityTypeBuilder<PendingRegistrationBusinessType> builder)
    {
        builder.ToTable("PendingRegistrationBusinessTypes");
        builder.HasKey(x => new { x.PendingRegistrationId, x.BusinessTypeId });

        builder.HasOne(x => x.PendingRegistration)
            .WithMany(x => x.BusinessTypes)
            .HasForeignKey(x => x.PendingRegistrationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.BusinessType)
            .WithMany()
            .HasForeignKey(x => x.BusinessTypeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
