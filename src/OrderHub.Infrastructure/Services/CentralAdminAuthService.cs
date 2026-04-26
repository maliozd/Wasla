using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderHub.Application.Abstractions.Admin;

namespace OrderHub.Infrastructure.Services;

public sealed class CentralAdminAuthService : ICentralAdminAuthService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CentralAdminAuthService> _logger;

    public CentralAdminAuthService(IConfiguration configuration, ILogger<CentralAdminAuthService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task<bool> ValidateAsync(string email, string password, CancellationToken ct)
    {
        // Environment variables use CentralAdmin__Email / CentralAdmin__PasswordHash,
        // but IConfiguration access is via section keys: CentralAdmin:Email / CentralAdmin:PasswordHash.
        var configuredEmail = _configuration["CentralAdmin:Email"]?.Trim();
        var hash = _configuration["CentralAdmin:PasswordHash"]?.Trim();
        var a = Environment.GetEnvironmentVariable("CentralAdmin__Email");
        if (string.IsNullOrWhiteSpace(configuredEmail) || string.IsNullOrWhiteSpace(hash))
        {
            _logger.LogWarning("Central admin login is not configured (missing CentralAdmin:Email or CentralAdmin:PasswordHash).");
            return Task.FromResult(false);
        }

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Task.FromResult(false);

        if (!string.Equals(email.Trim(), configuredEmail, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        try
        {
            var ok = BCrypt.Net.BCrypt.Verify(password, hash);
            return Task.FromResult(ok);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Central admin password verification failed.");
            return Task.FromResult(false);
        }
    }
}
