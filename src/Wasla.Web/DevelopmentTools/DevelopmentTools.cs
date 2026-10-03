using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Wasla.Web.DevelopmentTools;

/// <summary>
/// TEMPORARY local-testing tools. Everything defaults to off; appsettings.Development.json turns the tenant
/// reset on for local runs. Outside Development the tools stay unavailable whatever the configuration says.
/// </summary>
public sealed class DevelopmentToolsOptions
{
    public const string SectionName = "DevelopmentTools";

    /// <summary>Destructive: lets a tenant Owner reset the current test tenant to its post-provisioning state.</summary>
    public bool EnableTenantReset { get; set; }
}

public static class DevelopmentToolsAvailability
{
    /// <summary>Only in the Development environment, and only when explicitly enabled.</summary>
    public static bool IsTenantResetAvailable(IHostEnvironment environment, DevelopmentToolsOptions? options) =>
        environment.IsDevelopment() && options?.EnableTenantReset == true;
}

/// <summary>Marks a controller that must not exist at all unless the tenant reset tool is available.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DevelopmentTenantResetToolAttribute : Attribute
{
}

/// <summary>
/// Removes controllers marked with <see cref="DevelopmentTenantResetToolAttribute"/> at startup when the tool is
/// unavailable, so their routes do not exist: every request gets 404, before authentication, antiforgery or
/// any policy runs.
/// </summary>
public sealed class DevelopmentToolsConvention : IApplicationModelConvention
{
    private readonly bool _tenantResetAvailable;

    public DevelopmentToolsConvention(bool tenantResetAvailable)
    {
        _tenantResetAvailable = tenantResetAvailable;
    }

    public void Apply(ApplicationModel application)
    {
        if (_tenantResetAvailable)
            return;

        foreach (var controller in application.Controllers
                     .Where(controller => controller.Attributes.OfType<DevelopmentTenantResetToolAttribute>().Any())
                     .ToList())
        {
            application.Controllers.Remove(controller);
        }
    }
}
