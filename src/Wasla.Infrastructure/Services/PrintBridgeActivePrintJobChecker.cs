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

    public async Task<bool> HasActivePrintingJobAsync(Guid customerId, CancellationToken ct)
    {
        await using var db = await _tenantDbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        // Temporary conservative rule until stable Print Bridge device identity is available:
        // any in-progress print job blocks removal for the tenant.
        return await db.PrintJobs
            .AsNoTracking()
            .AnyAsync(j => j.Status == PrintJobStatus.Printing, ct)
            .ConfigureAwait(false);
    }
}
