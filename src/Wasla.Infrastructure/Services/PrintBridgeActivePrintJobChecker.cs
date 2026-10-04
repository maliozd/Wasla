using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class PrintBridgeActivePrintJobChecker : IPrintBridgeActivePrintJobChecker
{
    private readonly ITenantDbContextFactory _tenantDbFactory;

    public PrintBridgeActivePrintJobChecker(ITenantDbContextFactory tenantDbFactory)
    {
        _tenantDbFactory = tenantDbFactory;
    }

    public async Task<bool> HasActivePrintingJobAsync(
        Guid customerId,
        Guid? installationId,
        string? legacyLockedBy,
        CancellationToken ct)
    {
        await using var db = await _tenantDbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var normalizedLegacyName = string.IsNullOrWhiteSpace(legacyLockedBy)
            ? null
            : legacyLockedBy.Trim();

        return await db.PrintJobs
            .AsNoTracking()
            .AnyAsync(j => j.Status == PrintJobStatus.Printing
                && ((installationId.HasValue && j.LockedByInstallationId == installationId.Value)
                    || (j.LockedByInstallationId == null
                        && normalizedLegacyName != null
                        && j.LockedBy == normalizedLegacyName)), ct)
            .ConfigureAwait(false);
    }
}
