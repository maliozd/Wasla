using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Cli;

/// <summary>
/// <c>disable-central-admin</c> and <c>enable-central-admin</c>. The status change is saved through
/// <see cref="CentralDbContext"/>, which gives the admin a new security stamp in the same UPDATE statement: disabling
/// signs out every existing session, and enabling again does not bring any of them back. An account already in the
/// requested state is left untouched, so repeating a command is safe. Output names the account by masked email only.
/// </summary>
internal static class CliCentralAdminStatus
{
    public static async Task<int> ExecuteAsync(
        CentralDbContext central,
        string? email,
        bool enable,
        bool dryRun,
        TextWriter output,
        CancellationToken ct)
    {
        var emailNorm = (email ?? string.Empty).Trim();
        if (emailNorm.Length == 0)
        {
            output.WriteLine("Email is required.");
            return 2;
        }

        try
        {
            var normalized = emailNorm.ToUpperInvariant();
            var matches = await central.CentralAdminUsers
                .Where(x => x.NormalizedEmail == normalized)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (matches.Count == 0)
            {
                output.WriteLine("No matching user was found. No changes were made.");
                return 2;
            }

            if (matches.Count > 1)
            {
                output.WriteLine("More than one matching user was found. No changes were made.");
                return 2;
            }

            var user = matches[0];
            if (dryRun)
                output.WriteLine("Dry run: no changes will be written.");
            output.WriteLine("Scope: Central");
            output.WriteLine($"User: {CliPasswordReset.MaskEmail(user.Email)}");
            output.WriteLine($"Status: {(user.IsActive ? "Enabled" : "Disabled")}");

            if (user.IsActive == enable)
            {
                output.WriteLine(enable
                    ? "The account is already enabled. No changes were made."
                    : "The account is already disabled. No changes were made.");
                return 0;
            }

            if (dryRun)
            {
                output.WriteLine(enable
                    ? "The account would be enabled. Sessions issued before it was disabled would stay signed out."
                    : "The account would be disabled and all of its existing sessions signed out.");
                return 0;
            }

            user.IsActive = enable;
            user.UpdatedAt = DateTime.UtcNow;
            // CentralDbContext writes the new security stamp in this same UPDATE.
            await central.SaveChangesAsync(ct).ConfigureAwait(false);

            output.WriteLine(enable
                ? "Account enabled. Sessions issued before it was disabled stay signed out."
                : "Account disabled. All of its existing sessions are signed out.");
            return 0;
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException)
        {
            output.WriteLine("Database operation failed. Check the account with list-central-admins before retrying.");
            return 3;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.WriteLine("Account status change failed. Check the account with list-central-admins before retrying.");
            return 1;
        }
    }
}
