using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Security;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;

namespace Wasla.Cli;

internal static class CliPasswordReset
{
    public static async Task<int> ExecuteAsync(
        CentralDbContext central,
        string? scope,
        string? email,
        string? tenantSelector,
        bool dryRun,
        Func<Tenant, CancellationToken, Task<TenantDbContext>> openTenant,
        ICliPasswordReader passwords,
        CancellationToken ct)
    {
        if (!TryParseScope(scope, out var centralScope))
        {
            WriteError("Invalid scope. Use --scope central or --scope tenant.");
            return 2;
        }

        var emailNorm = (email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(emailNorm))
        {
            WriteError("Email is required.");
            return 2;
        }

        var selector = (tenantSelector ?? string.Empty).Trim();
        if (centralScope && selector.Length > 0)
        {
            WriteError("Central scope does not use --tenant.");
            return 2;
        }

        if (!centralScope && selector.Length == 0)
        {
            WriteError("Tenant scope requires --tenant <slug-or-id>.");
            return 2;
        }

        try
        {
            if (centralScope)
                return await ResetCentralAsync(central, emailNorm, dryRun, passwords, ct).ConfigureAwait(false);

            return await ResetTenantAsync(central, emailNorm, selector, dryRun, openTenant, passwords, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlException or DbUpdateException)
        {
            WriteError("Database operation failed. No password change was saved.");
            return 3;
        }
        catch (Exception)
        {
            WriteError("Password reset failed. No changes were made.");
            return 1;
        }
    }

    public static async Task<TenantDbContext> OpenTenantDatabaseAsync(
        ISecretManager secret,
        Tenant tenant,
        CancellationToken ct)
    {
        var plain = await secret.DecryptAsync(tenant.EncryptedConnectionString, tenant.EncryptionKeyVersion, ct)
            .ConfigureAwait(false);
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer(plain)
            .Options;
        return new TenantDbContext(options);
    }

    internal static string MaskEmail(string email)
    {
        var trimmed = email.Trim();
        var at = trimmed.IndexOf('@');
        if (at <= 0 || at >= trimmed.Length - 1)
            return "***";

        return trimmed[0] + "***" + trimmed[at..];
    }

    private static async Task<int> ResetCentralAsync(
        CentralDbContext central,
        string email,
        bool dryRun,
        ICliPasswordReader passwords,
        CancellationToken ct)
    {
        var normalized = email.ToUpperInvariant();
        var matches = await central.CentralAdminUsers
            .Where(x => x.NormalizedEmail == normalized)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (matches.Count == 0)
        {
            WriteError("No matching user was found. No changes were made.");
            return 2;
        }

        if (matches.Count > 1)
        {
            WriteError("More than one matching user was found. No changes were made.");
            return 2;
        }

        var user = matches[0];
        WriteTarget("Central", tenantName: null, user.Email, dryRun);
        if (dryRun)
            return 0;

        if (!TryReadConfirmedPassword(passwords, out var password))
            return 2;

        var policy = new DefaultPasswordPolicy().Validate(password);
        if (!policy.IsValid)
        {
            foreach (var error in policy.Errors)
                WriteError(DescribePolicyError(error));
            WriteError("No changes were made.");
            return 2;
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
        user.UpdatedAt = DateTime.UtcNow;
        await central.SaveChangesAsync(ct).ConfigureAwait(false);
        WriteSuccess();
        return 0;
    }

    private static async Task<int> ResetTenantAsync(
        CentralDbContext central,
        string email,
        string selector,
        bool dryRun,
        Func<Tenant, CancellationToken, Task<TenantDbContext>> openTenant,
        ICliPasswordReader passwords,
        CancellationToken ct)
    {
        List<Tenant> tenants;
        if (Guid.TryParse(selector, out var tenantId))
        {
            tenants = await central.Tenants
                .Where(t => t.Id == tenantId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
        else
        {
            tenants = await central.Tenants
                .Where(t => t.Slug == selector)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        if (tenants.Count == 0)
        {
            WriteError("Unknown tenant. No changes were made.");
            return 2;
        }

        if (tenants.Count > 1)
        {
            WriteError("More than one matching tenant was found. No changes were made.");
            return 2;
        }

        var tenant = tenants[0];
        await using var db = await openTenant(tenant, ct).ConfigureAwait(false);
        var matches = await db.AppUsers
            .Where(u => u.Email == email)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (matches.Count == 0)
        {
            WriteError("No matching user was found. No changes were made.");
            return 2;
        }

        if (matches.Count > 1)
        {
            WriteError("More than one matching user was found. No changes were made.");
            return 2;
        }

        var user = matches[0];
        WriteTarget("Tenant", tenant.Name, user.Email, dryRun);
        if (dryRun)
            return 0;

        if (!TryReadConfirmedPassword(passwords, out var password))
            return 2;

        var policy = new DefaultPasswordPolicy().Validate(password);
        if (!policy.IsValid)
        {
            foreach (var error in policy.Errors)
                WriteError(DescribePolicyError(error));
            WriteError("No changes were made.");
            return 2;
        }

        var now = DateTime.UtcNow;
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
        // Ends every existing Web session of this user.
        user.SecurityStamp = Guid.NewGuid();
        user.UpdatedAt = now;

        var activeTokens = await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAtUtc == null && t.ExpiresAtUtc > now)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var token in activeTokens)
            token.UsedAtUtc = now;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        WriteSuccess();
        return 0;
    }

    private static bool TryReadConfirmedPassword(ICliPasswordReader passwords, out string password)
    {
        password = string.Empty;
        var first = passwords.ReadSecret("New password: ");
        if (!first.Ok)
        {
            WriteError("Interactive password entry is required. The new password is not accepted as a command argument.");
            return false;
        }

        var second = passwords.ReadSecret("Confirm new password: ");
        if (!second.Ok)
        {
            WriteError("Interactive password entry is required. The new password is not accepted as a command argument.");
            return false;
        }

        if (!string.Equals(first.Value, second.Value, StringComparison.Ordinal))
        {
            WriteError("New password and confirmation do not match. No changes were made.");
            return false;
        }

        password = first.Value;
        return true;
    }

    private static void WriteTarget(string scope, string? tenantName, string email, bool dryRun)
    {
        if (dryRun)
            Console.WriteLine("Dry run: password would be reset. No changes were written.");

        Console.WriteLine($"Scope: {scope}");
        if (tenantName is not null)
            Console.WriteLine($"Tenant: {tenantName}");
        Console.WriteLine($"User: {MaskEmail(email)}");
    }

    private static void WriteSuccess()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Password updated successfully.");
        Console.ResetColor();
    }

    private static string DescribePolicyError(string key) => key switch
    {
        "Validation.PasswordRequired" => "Password is required.",
        "Validation.PasswordMinLength" => "Password must be at least 8 characters.",
        _ => key
    };

    private static bool TryParseScope(string? scope, out bool centralScope)
    {
        if (string.Equals(scope, "central", StringComparison.OrdinalIgnoreCase))
        {
            centralScope = true;
            return true;
        }

        if (string.Equals(scope, "tenant", StringComparison.OrdinalIgnoreCase))
        {
            centralScope = false;
            return true;
        }

        centralScope = false;
        return false;
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}
