using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Wasla.Web.Reports.Export;

public static class ReportExportServiceCollectionExtensions
{
    /// <summary>The services of the report export endpoint (WAS-184): the process-wide export gate.</summary>
    public static IServiceCollection AddWaslaReportExports(this IServiceCollection services)
    {
        services.TryAddSingleton<ReportExportGate>();
        return services;
    }
}
