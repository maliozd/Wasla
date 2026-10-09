using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;
using Wasla.Contracts.Auth;
using Wasla.Contracts.Enums;

namespace Wasla.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IAuthValidationService _authValidation;

    public AuthController(ICurrentTenantService currentTenant, IAuthValidationService authValidation)
    {
        _currentTenant = currentTenant;
        _authValidation = authValidation;
    }

    // API login endpoint remains for API clients; cookie issuance for browser UI is handled by Wasla.Web.
    [HttpPost("validate")]
    [AllowAnonymous]
    public async Task<IActionResult> Validate([FromBody] LoginRequest request, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound("Tenant not found");

        var session = await _authValidation.ValidateAsync(tenant.Id, request.Email, request.Password, ct);
        return session is null ? Unauthorized() : Ok();
    }

    // Any current tenant user: the session has been revalidated, and the role must be an assignable one.
    [Authorize(Policy = WaslaTenantPolicies.AuthenticatedTenantUser)]
    [HttpGet("me")]
    public ActionResult<CurrentUserDto> Me()
    {
        var tenant = _currentTenant.CurrentTenant;
        var tenantClaim = User.FindFirst(WaslaAuthContracts.TenantIdClaim)?.Value;
        if (tenant is null
            || !Guid.TryParse(tenantClaim, out var claimTenantId)
            || claimTenantId != tenant.Id)
        {
            return Forbid();
        }

        var userIdClaim =
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
            User.FindFirst("UserId")?.Value;

        var email =
            User.FindFirst(ClaimTypes.Email)?.Value ??
            User.FindFirst("Email")?.Value ??
            string.Empty;

        var fullName =
            User.FindFirst(ClaimTypes.Name)?.Value ??
            "User";

        var role =
            User.FindFirst(ClaimTypes.Role)?.Value ??
            User.FindFirst("Role")?.Value ??
            "Viewer";

        _ = Guid.TryParse(userIdClaim, out var userId);
        _ = Enum.TryParse<UserRoleDto>(role, ignoreCase: true, out var roleDto);

        return Ok(new CurrentUserDto(userId, email, fullName, roleDto));
    }
}

