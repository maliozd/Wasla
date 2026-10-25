using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Infrastructure.Security;

namespace Wasla.Web.Security;

/// <summary>
/// Web's tenant session revalidation: the shared <see cref="TenantSessionCookieEvents"/>, with the cookie handler's
/// default challenge and forbid behavior (redirects to the scheme's <c>LoginPath</c> and <c>AccessDeniedPath</c> on the
/// same host). A tenant database failure surfaces as <see cref="TenantSessionUnavailableException"/> to the global
/// exception handler.
/// </summary>
public sealed class TenantCookieEvents(
    ICurrentTenantService currentTenant,
    ITenantSessionValidator validator,
    ILogger<TenantCookieEvents> logger)
    : TenantSessionCookieEvents(currentTenant, validator, logger);
