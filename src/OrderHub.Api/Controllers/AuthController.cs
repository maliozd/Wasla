using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Application.Auth.Services;

namespace OrderHub.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    public sealed record LoginRequest(string Email, string Password);

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

    [HttpGet("me")]
    public IActionResult Me()
    {
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            return Unauthorized();
        }

        var customerId = User.FindFirst("CustomerId")?.Value;
        var userId = User.FindFirst("UserId")?.Value;
        var role = User.FindFirst("Role")?.Value;
        var email = User.FindFirst("Email")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

        return Ok(new { customerId, userId, role, email });
    }
}

