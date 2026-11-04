using Wasla.Web.Middleware;

namespace Wasla.Web.Security;

/// <summary>
/// Marks the tenant logout actions: the logout page and the logout itself. When the tenant database fails while such a
/// request's session is checked or its revocation is stored, <see cref="TenantLogoutFailureMiddleware"/> answers with the
/// logout failure page instead of the generic error page.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TenantLogoutAttribute : Attribute;
