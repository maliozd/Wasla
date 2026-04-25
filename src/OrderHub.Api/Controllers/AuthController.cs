using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Auth.Services;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Contracts.Auth;
using OrderHub.Contracts.Enums;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ICurrentCustomerService _currentCustomerService;
    private readonly ICustomerDbContextFactory _customerDbFactory;

    public AuthController(
        IAuthService authService,
        ICurrentCustomerService currentCustomerService,
        ICustomerDbContextFactory customerDbFactory)
    {
        _authService = authService;
        _currentCustomerService = currentCustomerService;
        _customerDbFactory = customerDbFactory;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var ok = await _authService.LoginAsync(request.Email, request.Password, ct);
        return ok ? Ok() : Unauthorized();
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await _authService.LogoutAsync(ct);
        return Ok();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<CurrentUserDto>> Me(CancellationToken ct)
    {
        var customer = _currentCustomerService.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        var userIdClaim =
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
            User.FindFirst("UserId")?.Value;

        if (!Guid.TryParse(userIdClaim, out var userId)) return Unauthorized();

        await using var db = await _customerDbFactory.CreateAsync(customer.Id, ct);
        var user = await db.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);

        if (user is null) return Unauthorized();

        return Ok(new CurrentUserDto(
            user.Id,
            user.Email,
            user.FullName,
            (UserRoleDto)(int)user.Role));
    }
}

