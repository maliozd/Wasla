using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

/// <summary>
/// Destruction operation records (WAS-222). Identifiers and codes only. No foreign key to the destroyed row, which no
/// longer exists, or to the administrator, whose account may be removed while the record must stay.
/// </summary>
public sealed class DataDestructionRecordConfiguration : IEntityTypeConfiguration<DataDestructionRecord>
{
    public void Configure(EntityTypeBuilder<DataDestructionRecord> builder)
    {
        builder.ToTable("DataDestructionRecords");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Subject).HasConversion<int>();
        builder.Property(x => x.Method).HasConversion<int>();
        builder.Property(x => x.Result).HasConversion<int>();
        builder.Property(x => x.Actor).HasConversion<int>();

        // Which operations touched one record; and the records whose own retention has ended, oldest first.
        builder.HasIndex(x => x.SubjectId).HasDatabaseName("IX_DataDestructionRecords_SubjectId");
        builder.HasIndex(x => new { x.RetainUntilUtc, x.Id }).HasDatabaseName("IX_DataDestructionRecords_RetainUntilUtc");
    }
}
