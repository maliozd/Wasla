using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Domain.Entities.Central;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Cli;

internal static class CliCommands
{
    /// <summary>
    /// Prints a BCrypt hash for CentralAdmin PasswordHash configuration (no master key required).
    /// </summary>
    public static int HashPassword(string[] args)
    {
        string? password = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--password", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                password = args[i + 1];
                break;
            }
        }

        if (string.IsNullOrEmpty(password))
        {
            WriteError("Usage: hash-password --password <plaintext>");
            return 2;
        }

        Console.WriteLine(BCrypt.Net.BCrypt.HashPassword(password));
        return 0;
    }

    private static readonly Regex SlugRegex = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex SqlDbNameRegex = new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    private static bool IsProductionEnvironment()
    {
        var env =
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
            string.Empty;

        return string.Equals(env, "Production", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CheckDestructiveSafety(bool confirm, bool forceProduction)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("WARNING: This will permanently delete data.");
        Console.WriteLine("This will permanently delete the customer record and/or the customer database.");
        Console.ResetColor();

        if (!confirm)
        {
            WriteError("Refusing to run destructive command without --confirm.");
            return false;
        }

        if (IsProductionEnvironment() && !forceProduction)
        {
            WriteError("Refusing to run destructive command in Production. Use --force-production only if you know exactly what you are doing.");
            return false;
        }

        return true;
    }

    public static async Task<int> AddCustomerAsync(
        IHost host,
        string name,
        string slug,
        string domain,
        string adminEmail,
        string adminPassword,
        string adminName,
        string? sqlServer,
        string sqlAuth,
        CancellationToken ct)
    {
        if (!SlugRegex.IsMatch(slug))
        {
            WriteError("Slug must match ^[a-zA-Z0-9_-]+$.");
            return 2;
        }

        var pascal = SlugToPascalCase(slug);
        var dbName = $"OrderHub_{pascal}";
        if (!SqlDbNameRegex.IsMatch(dbName))
        {
            WriteError($"Derived database name '{dbName}' is invalid.");
            return 2;
        }

        var server = string.IsNullOrWhiteSpace(sqlServer)
            ? host.Services.GetRequiredService<IConfiguration>()["CustomerDb:ServerInstance"]?.Trim() ?? "."
            : sqlServer.Trim();

        var dbCreated = false;
        Customer? insertedCentral = null;

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var exists = await central.Customers
                .AsNoTracking()
                .AnyAsync(c => c.Slug == slug || c.PrimaryDomain == domain, ct)
                .ConfigureAwait(false);
            if (exists)
            {
                WriteError("A customer with this slug or primary domain already exists.");
                return 2;
            }

            WriteLineStep($"Target database name: {dbName}");

            await EnsureDatabaseExistsAsync(server, sqlAuth, dbName, ct).ConfigureAwait(false);
            dbCreated = true;

            var customerConnString = BuildCustomerConnectionString(server, sqlAuth, dbName);
            var options = new DbContextOptionsBuilder<CustomerDbContext>()
                .UseSqlServer(customerConnString)
                .Options;
            var migrationNow = DateTime.UtcNow;
            string migrationResult;
            try
            {
                await using var db = new CustomerDbContext(options);
                WriteLineStep("Applying CustomerDb migrations…");
                await db.Database.MigrateAsync(ct).ConfigureAwait(false);
                migrationResult = "Success";
            }
            catch (Exception ex)
            {
                migrationResult = "Failed: " + SanitizeMigrationError(ex);
                throw;
            }

            WriteLineStep("Encrypting connection string…");
            var (encrypted, keyVersion) = await secret.EncryptAsync(customerConnString, ct).ConfigureAwait(false);

            var customerId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var customer = new Customer
            {
                Id = customerId,
                Name = name,
                Slug = slug,
                PrimaryDomain = domain,
                DatabaseName = dbName,
                EncryptedConnectionString = encrypted,
                EncryptionKeyVersion = keyVersion,
                SchemaVersion = MigrationMetadata.CurrentCustomerDbSchemaVersion,
                LastMigrationAt = migrationNow,
                LastMigrationResult = migrationResult,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };

            central.Customers.Add(customer);
            await central.SaveChangesAsync(ct).ConfigureAwait(false);
            insertedCentral = customer;

            await using (var userDb = new CustomerDbContext(options))
            {
                WriteLineStep("Creating admin user…");
                var appUser = new AppUser
                {
                    Email = adminEmail.Trim(),
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
                    FullName = string.IsNullOrWhiteSpace(adminName) ? "Admin" : adminName.Trim(),
                    Role = UserRole.Owner,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                userDb.AppUsers.Add(appUser);
                await userDb.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Customer created successfully");
            Console.ResetColor();
            Console.WriteLine($"""
                Id:       {customerId}
                Name:     {name}
                Domain:   {domain}
                Database: {dbName}
                Admin:    {adminEmail}  (password set)
                Next steps:
                - Ensure DNS points {domain} to your server
                - Log in at https://{domain}/auth/login
                """);

            return 0;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            WriteError(ex.Message);
            if (dbCreated)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    insertedCentral is not null
                        ? $"Warning: partial failure. Drop database [{dbName}] and delete CentralDb customer row Id={insertedCentral.Id} if you need to retry clean."
                        : "Warning: database was created or modified. If registration failed partway, manually drop the database and remove the Customer row from CentralDb if it was inserted.");
                Console.ResetColor();
            }
            return 3;
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    public static async Task<int> MigrateCentralAsync(IHost host, CancellationToken ct)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();

            var pending = (await central.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            WriteLineStep("CentralDb migration check");
            Console.WriteLine($"Pending: {pending.Count}");
            if (pending.Count > 0)
            {
                foreach (var m in pending)
                    Console.WriteLine($"  {m}");
                WriteLineStep("Applying migrations…");
            }

            await central.Database.MigrateAsync(ct).ConfigureAwait(false);

            var applied = (await central.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            var latest = applied.Count > 0 ? applied[^1] : "(none)";
            Console.WriteLine($"Latest applied migration: {latest}");
            if (pending.Count == 0)
                Console.WriteLine("CentralDb was already up to date.");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ migrate-central completed successfully.");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            WriteError($"migrate-central failed: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> MigrateCustomerAsync(
        IHost host,
        string? slug,
        string? customerIdArg,
        CancellationToken ct)
    {
        var hasSlug = !string.IsNullOrWhiteSpace(slug);
        if (hasSlug && !string.IsNullOrWhiteSpace(customerIdArg))
        {
            WriteError("Use either --slug or --customer-id, not both.");
            return 2;
        }
        if (!hasSlug && string.IsNullOrWhiteSpace(customerIdArg))
        {
            WriteError("Specify --slug or --customer-id.");
            return 2;
        }
        if (!string.IsNullOrWhiteSpace(slug) && !SlugRegex.IsMatch(slug))
        {
            WriteError("Slug must match ^[a-zA-Z0-9_-]+$.");
            return 2;
        }

        Guid? customerId = null;
        if (!string.IsNullOrWhiteSpace(customerIdArg))
        {
            if (!Guid.TryParse(customerIdArg, out var g))
            {
                WriteError("--customer-id must be a valid GUID.");
                return 2;
            }
            customerId = g;
        }

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var slugNorm = slug?.Trim() ?? string.Empty;
            var customer = customerId is { } id
                ? await central.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
                : await central.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slugNorm, ct).ConfigureAwait(false);

            if (customer is null)
            {
                WriteError("Customer not found.");
                return 2;
            }
            if (!customer.IsActive)
            {
                WriteError("Customer is inactive.");
                return 2;
            }

            WriteLineStep($"Customer: {customer.Name}  slug={customer.Slug}  database={customer.DatabaseName}");

            var plain = await secret.DecryptAsync(customer.EncryptedConnectionString, customer.EncryptionKeyVersion, ct)
                .ConfigureAwait(false);
            var options = new DbContextOptionsBuilder<CustomerDbContext>()
                .UseSqlServer(plain)
                .Options;

            await using (var db = new CustomerDbContext(options))
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
                Console.WriteLine($"Pending CustomerDb migrations: {pending.Count}");
                foreach (var m in pending)
                    Console.WriteLine($"  {m}");

                WriteLineStep("Applying CustomerDb migrations…");
                await db.Database.MigrateAsync(ct).ConfigureAwait(false);

                var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
                var latest = applied.Count > 0 ? applied[^1] : "(none)";
                Console.WriteLine($"Latest applied CustomerDb migration: {latest}");
            }

            var tracked = await central.Customers.FirstOrDefaultAsync(c => c.Id == customer.Id, ct).ConfigureAwait(false);
            if (tracked is not null)
            {
                var when = DateTime.UtcNow;
                tracked.LastMigrationAt = when;
                tracked.LastMigrationResult = "Success";
                tracked.SchemaVersion = MigrationMetadata.CurrentCustomerDbSchemaVersion;
                tracked.UpdatedAt = when;
                await central.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ migrate-customer completed successfully.");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                using var scope2 = host.Services.CreateScope();
                var central2 = scope2.ServiceProvider.GetRequiredService<CentralDbContext>();
                Guid? idForTrack = customerId;
                if (idForTrack is null && !string.IsNullOrWhiteSpace(slug))
                {
                    var slugN = slug.Trim();
                    var c2 = await central2.Customers.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Slug == slugN, ct)
                        .ConfigureAwait(false);
                    idForTrack = c2?.Id;
                }
                if (idForTrack is { } trackId)
                {
                    var tracked = await central2.Customers.FirstOrDefaultAsync(c => c.Id == trackId, ct).ConfigureAwait(false);
                    if (tracked is not null)
                    {
                        tracked.LastMigrationAt = DateTime.UtcNow;
                        tracked.LastMigrationResult = "Failed: " + SanitizeMigrationError(ex);
                        tracked.UpdatedAt = DateTime.UtcNow;
                        await central2.SaveChangesAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // ignore secondary failure
            }

            WriteError($"migrate-customer failed: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> MigrateAllCustomersAsync(
        IHost host,
        bool dryRun,
        string[]? onlySlugs,
        CancellationToken ct)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var query = central.Customers.AsNoTracking().Where(c => c.IsActive);
            var list = (await query.ToListAsync(ct).ConfigureAwait(false)).OrderBy(c => c.Slug).ToList();

            if (onlySlugs is { Length: > 0 })
            {
                var set = onlySlugs.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                list = list.Where(c => set.Contains(c.Slug)).ToList();
                foreach (var s in set)
                {
                    if (list.All(c => !string.Equals(c.Slug, s, StringComparison.OrdinalIgnoreCase)))
                    {
                        WriteError($"Customer slug not found: {s}");
                        return 2;
                    }
                }
            }

            var total = list.Count;
            Console.WriteLine($"Active customers to process: {total}");
            foreach (var c in list)
                Console.WriteLine($"  - {c.Name}  ({c.Slug})  [{c.DatabaseName}]");

            if (dryRun)
            {
                foreach (var c in list)
                {
                    try
                    {
                        var plain = await secret.DecryptAsync(c.EncryptedConnectionString, c.EncryptionKeyVersion, ct).ConfigureAwait(false);
                        var options = new DbContextOptionsBuilder<CustomerDbContext>().UseSqlServer(plain).Options;
                        await using var db = new CustomerDbContext(options);
                        var p = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
                        if (p.Count == 0)
                            Console.WriteLine($"  (dry-run) {c.Slug}: up to date");
                        else
                            Console.WriteLine($"  (dry-run) {c.Slug}: would apply {p.Count} migration(s): {string.Join(", ", p)}");
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  (dry-run) {c.Slug}: {ex.Message}");
                        Console.ResetColor();
                    }
                }
                return 0;
            }

            var succeeded = 0;
            var failed = new List<(string Slug, string Name)>();

            foreach (var c in list)
            {
                try
                {
                    var plain = await secret.DecryptAsync(c.EncryptedConnectionString, c.EncryptionKeyVersion, ct)
                        .ConfigureAwait(false);
                    var options = new DbContextOptionsBuilder<CustomerDbContext>()
                        .UseSqlServer(plain)
                        .Options;
                    await using var db = new CustomerDbContext(options);

                    await db.Database.MigrateAsync(ct).ConfigureAwait(false);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✓ {c.Slug}  ({c.DatabaseName})");
                    Console.ResetColor();

                    var tracked = await central.Customers.FirstOrDefaultAsync(x => x.Id == c.Id, ct).ConfigureAwait(false);
                    if (tracked is not null)
                    {
                        var when = DateTime.UtcNow;
                        tracked.LastMigrationAt = when;
                        tracked.LastMigrationResult = "Success";
                        tracked.SchemaVersion = MigrationMetadata.CurrentCustomerDbSchemaVersion;
                        tracked.UpdatedAt = when;
                        await central.SaveChangesAsync(ct).ConfigureAwait(false);
                    }
                    succeeded++;
                }
                catch (Exception ex)
                {
                    failed.Add((c.Slug, c.Name));
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"✗ {c.Slug}: {ex.Message}");
                    Console.ResetColor();

                    try
                    {
                        var tracked = await central.Customers.FirstOrDefaultAsync(x => x.Id == c.Id, ct).ConfigureAwait(false);
                        if (tracked is not null)
                        {
                            var when = DateTime.UtcNow;
                            tracked.LastMigrationAt = when;
                            tracked.LastMigrationResult = "Failed: " + SanitizeMigrationError(ex);
                            tracked.UpdatedAt = when;
                            await central.SaveChangesAsync(ct).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("--- Summary ---");
            Console.WriteLine($"Total:     {total}");
            Console.WriteLine($"Succeeded: {succeeded}");
            Console.WriteLine($"Failed:    {failed.Count}");
            if (failed.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Failed customers (slug / name):");
                foreach (var f in failed)
                    Console.WriteLine($"  - {f.Slug}  /  {f.Name}");
                Console.ResetColor();
            }

            return failed.Count > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    public static async Task<int> MigrationStatusAsync(IHost host, CancellationToken ct)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            Console.WriteLine("=== CentralDb ===");
            var centralPending = (await central.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            var centralApplied = (await central.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            var centralLatest = centralApplied.Count > 0 ? centralApplied[^1] : "(none)";
            Console.WriteLine($"Latest applied: {centralLatest}");
            Console.WriteLine($"Pending count:  {centralPending.Count}");
            if (centralPending.Count > 0)
            {
                foreach (var m in centralPending)
                    Console.WriteLine($"  {m}");
            }

            var customers = await central.Customers
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Slug)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("=== Customer databases (active) ===");
            foreach (var c in customers)
            {
                Console.WriteLine();
                Console.WriteLine($"{c.Name}  |  slug: {c.Slug}  |  db: {c.DatabaseName}");
                Console.WriteLine(
                    $"  CentralDb tracking: LastMigrationAt={c.LastMigrationAt:u}  Result={c.LastMigrationResult ?? "(null)"}  SchemaVersion={c.SchemaVersion}");
                try
                {
                    var plain = await secret.DecryptAsync(c.EncryptedConnectionString, c.EncryptionKeyVersion, ct).ConfigureAwait(false);
                    var options = new DbContextOptionsBuilder<CustomerDbContext>().UseSqlServer(plain).Options;
                    await using var db = new CustomerDbContext(options);
                    var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
                    var latest = applied.Count > 0 ? applied[^1] : "(none)";
                    var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
                    Console.WriteLine($"  CustomerDb latest applied: {latest}");
                    Console.WriteLine($"  Pending: {pending.Count}");
                    if (pending.Count > 0)
                    {
                        foreach (var m in pending)
                            Console.WriteLine($"    {m}");
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  (could not read migration status) {ex.Message}");
                    Console.ResetColor();
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    public static async Task<int> ListCustomersAsync(IHost host, CancellationToken ct)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var rows = await central.Customers
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Slug)
                .Select(c => new { c.Id, c.Name, c.Slug, c.PrimaryDomain, c.DatabaseName, c.IsActive })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            const string hId = "ID (short)", hName = "Name", hSlug = "Slug", hDomain = "Domain", hDb = "Database", hAct = "Active";
            Console.WriteLine($"{hId.PadRight(9)} | {hName.PadRight(15)} | {hSlug.PadRight(12)} | {hDomain.PadRight(28)} | {hDb.PadRight(20)} | {hAct}");
            foreach (var r in rows)
            {
                var shortId = r.Id.ToString("N")[..8];
                Console.WriteLine(
                    $"{shortId.PadRight(9)} | {Truncate(r.Name, 15).PadRight(15)} | {Truncate(r.Slug, 12).PadRight(12)} | {Truncate(r.PrimaryDomain, 28).PadRight(28)} | {Truncate(r.DatabaseName, 20).PadRight(20)} | {(r.IsActive ? "yes" : "no")}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    public static async Task<int> EncryptAsync(IHost host, string plaintext, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            WriteError("Plaintext is required.");
            return 2;
        }

        try
        {
            using var scope = host.Services.CreateScope();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();
            var (enc, ver) = await secret.EncryptAsync(plaintext, ct).ConfigureAwait(false);
            Console.WriteLine($"KeyVersion: {ver}");
            Console.WriteLine($"Encrypted: {enc}");
            return 0;
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    public static async Task<int> CreateUserAsync(
        IHost host,
        string customerSlug,
        string email,
        string password,
        string role,
        string? fullName,
        CancellationToken ct)
    {
        if (!SlugRegex.IsMatch(customerSlug))
        {
            WriteError("Customer slug must match ^[a-zA-Z0-9_-]+$.");
            return 2;
        }

        if (!Enum.TryParse<UserRole>(role, true, out var userRole) ||
            userRole is not (UserRole.Owner or UserRole.Manager or UserRole.Staff))
        {
            WriteError("Role must be one of: Owner, Manager, Staff.");
            return 2;
        }

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var customer = await central.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Slug == customerSlug, ct)
                .ConfigureAwait(false);
            if (customer is null)
            {
                WriteError("Customer not found for slug.");
                return 2;
            }
            if (!customer.IsActive)
            {
                WriteError("Customer is inactive.");
                return 2;
            }

            var plain = await secret.DecryptAsync(customer.EncryptedConnectionString, customer.EncryptionKeyVersion, ct)
                .ConfigureAwait(false);
            var options = new DbContextOptionsBuilder<CustomerDbContext>()
                .UseSqlServer(plain)
                .Options;
            await using var db = new CustomerDbContext(options);

            var emailNorm = email.Trim();
            if (await db.AppUsers.AnyAsync(u => u.Email == emailNorm, ct).ConfigureAwait(false))
            {
                WriteError("A user with this email already exists.");
                return 2;
            }

            var now = DateTime.UtcNow;
            var user = new AppUser
            {
                Email = emailNorm,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                FullName = string.IsNullOrWhiteSpace(fullName) ? string.Empty : fullName.Trim(),
                Role = userRole,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.AppUsers.Add(user);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ User {emailNorm} created (role: {userRole}).");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex) when (ex is SqlException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            WriteError(ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    public static async Task<int> DeleteCustomerAsync(
        IHost host,
        string? slug,
        string? customerIdArg,
        bool confirm,
        bool forceProduction,
        CancellationToken ct)
    {
        if (!CheckDestructiveSafety(confirm, forceProduction))
            return 2;

        var hasSlug = !string.IsNullOrWhiteSpace(slug);
        if (hasSlug && !string.IsNullOrWhiteSpace(customerIdArg))
        {
            WriteError("Use either --slug or --customer-id, not both.");
            return 2;
        }
        if (!hasSlug && string.IsNullOrWhiteSpace(customerIdArg))
        {
            WriteError("Specify --slug or --customer-id.");
            return 2;
        }
        if (hasSlug && !SlugRegex.IsMatch(slug!))
        {
            WriteError("Slug must match ^[a-zA-Z0-9_-]+$.");
            return 2;
        }

        Guid? customerId = null;
        if (!string.IsNullOrWhiteSpace(customerIdArg))
        {
            if (!Guid.TryParse(customerIdArg, out var g))
            {
                WriteError("--customer-id must be a valid GUID.");
                return 2;
            }
            customerId = g;
        }

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var slugNorm = slug?.Trim() ?? string.Empty;
            var customer = customerId is { } id
                ? await central.Customers.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
                : await central.Customers.FirstOrDefaultAsync(c => c.Slug == slugNorm, ct).ConfigureAwait(false);

            if (customer is null)
            {
                WriteError("Customer not found.");
                return 2;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"About to delete customer '{customer.Name}' (slug={customer.Slug}) and drop database '{customer.DatabaseName}'.");
            Console.ResetColor();

            var plain = await secret.DecryptAsync(customer.EncryptedConnectionString, customer.EncryptionKeyVersion, ct)
                .ConfigureAwait(false);
            var options = new DbContextOptionsBuilder<CustomerDbContext>()
                .UseSqlServer(plain)
                .Options;

            await using (var db = new CustomerDbContext(options))
            {
                WriteLineStep("Dropping CustomerDb (if exists)…");
                await db.Database.EnsureDeletedAsync(ct).ConfigureAwait(false);
            }

            WriteLineStep("Deleting CentralDb customer record…");
            central.Customers.Remove(customer);
            await central.SaveChangesAsync(ct).ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Customer deleted successfully.");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("delete-customer failed: " + ex.Message);
            return 1;
        }
    }

    public static async Task<int> ResetCustomerDbAsync(
        IHost host,
        string? slug,
        string? customerIdArg,
        bool confirm,
        bool forceProduction,
        CancellationToken ct)
    {
        if (!CheckDestructiveSafety(confirm, forceProduction))
            return 2;

        var hasSlug = !string.IsNullOrWhiteSpace(slug);
        if (hasSlug && !string.IsNullOrWhiteSpace(customerIdArg))
        {
            WriteError("Use either --slug or --customer-id, not both.");
            return 2;
        }
        if (!hasSlug && string.IsNullOrWhiteSpace(customerIdArg))
        {
            WriteError("Specify --slug or --customer-id.");
            return 2;
        }
        if (hasSlug && !SlugRegex.IsMatch(slug!))
        {
            WriteError("Slug must match ^[a-zA-Z0-9_-]+$.");
            return 2;
        }

        Guid? customerId = null;
        if (!string.IsNullOrWhiteSpace(customerIdArg))
        {
            if (!Guid.TryParse(customerIdArg, out var g))
            {
                WriteError("--customer-id must be a valid GUID.");
                return 2;
            }
            customerId = g;
        }

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var slugNorm = slug?.Trim() ?? string.Empty;
            var customer = customerId is { } id
                ? await central.Customers.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
                : await central.Customers.FirstOrDefaultAsync(c => c.Slug == slugNorm, ct).ConfigureAwait(false);

            if (customer is null)
            {
                WriteError("Customer not found.");
                return 2;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"About to RESET database '{customer.DatabaseName}' for customer '{customer.Name}' (slug={customer.Slug}).");
            Console.ResetColor();

            var plain = await secret.DecryptAsync(customer.EncryptedConnectionString, customer.EncryptionKeyVersion, ct)
                .ConfigureAwait(false);
            var options = new DbContextOptionsBuilder<CustomerDbContext>()
                .UseSqlServer(plain)
                .Options;

            var migrationNow = DateTime.UtcNow;
            var migrationResult = "Unknown";

            try
            {
                await using var db = new CustomerDbContext(options);
                WriteLineStep("Dropping CustomerDb (if exists)…");
                await db.Database.EnsureDeletedAsync(ct).ConfigureAwait(false);

                WriteLineStep("Applying CustomerDb migrations…");
                await db.Database.MigrateAsync(ct).ConfigureAwait(false);
                migrationResult = "Success";
            }
            catch (Exception ex)
            {
                migrationResult = "Failed: " + SanitizeMigrationError(ex);
                throw;
            }
            finally
            {
                var tracked = await central.Customers.FirstOrDefaultAsync(x => x.Id == customer.Id, ct).ConfigureAwait(false);
                if (tracked is not null)
                {
                    tracked.LastMigrationAt = migrationNow;
                    tracked.LastMigrationResult = migrationResult;
                    tracked.SchemaVersion = MigrationMetadata.CurrentCustomerDbSchemaVersion;
                    tracked.UpdatedAt = DateTime.UtcNow;
                    await central.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Customer database reset successfully (schema migrated).");
            Console.ResetColor();
            Console.WriteLine("Note: All users/orders/platform connections/branches were deleted. Recreate admin user if needed (seed-customer-admin).");
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("reset-customer-db failed: " + ex.Message);
            return 1;
        }
    }

    public static async Task<int> ResetAllCustomerDbsAsync(
        IHost host,
        bool confirm,
        bool forceProduction,
        CancellationToken ct)
    {
        if (!CheckDestructiveSafety(confirm, forceProduction))
            return 2;

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var customers = await central.Customers
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Slug)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var total = customers.Count;
            var succeeded = 0;
            var failed = new List<(string Slug, string Name)>();

            Console.WriteLine($"Active customers to reset: {total}");

            foreach (var c in customers)
            {
                try
                {
                    var plain = await secret.DecryptAsync(c.EncryptedConnectionString, c.EncryptionKeyVersion, ct).ConfigureAwait(false);
                    var options = new DbContextOptionsBuilder<CustomerDbContext>().UseSqlServer(plain).Options;

                    var migrationNow = DateTime.UtcNow;
                    var migrationResult = "Unknown";

                    try
                    {
                        await using var db = new CustomerDbContext(options);
                        await db.Database.EnsureDeletedAsync(ct).ConfigureAwait(false);
                        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
                        migrationResult = "Success";
                    }
                    catch (Exception ex)
                    {
                        migrationResult = "Failed: " + SanitizeMigrationError(ex);
                        throw;
                    }
                    finally
                    {
                        var tracked = await central.Customers.FirstOrDefaultAsync(x => x.Id == c.Id, ct).ConfigureAwait(false);
                        if (tracked is not null)
                        {
                            tracked.LastMigrationAt = migrationNow;
                            tracked.LastMigrationResult = migrationResult;
                            tracked.SchemaVersion = MigrationMetadata.CurrentCustomerDbSchemaVersion;
                            tracked.UpdatedAt = DateTime.UtcNow;
                            await central.SaveChangesAsync(ct).ConfigureAwait(false);
                        }
                    }

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✓ {c.Slug}  ({c.DatabaseName})");
                    Console.ResetColor();
                    succeeded++;
                }
                catch (Exception ex)
                {
                    failed.Add((c.Slug, c.Name));
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"✗ {c.Slug}: {ex.Message}");
                    Console.ResetColor();
                }
            }

            Console.WriteLine();
            Console.WriteLine("--- Summary ---");
            Console.WriteLine($"Total:     {total}");
            Console.WriteLine($"Succeeded: {succeeded}");
            Console.WriteLine($"Failed:    {failed.Count}");
            if (failed.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Failed customers (slug / name):");
                foreach (var f in failed)
                    Console.WriteLine($"  - {f.Slug}  /  {f.Name}");
                Console.ResetColor();
            }

            return failed.Count > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            WriteError("reset-all-customer-dbs failed: " + ex.Message);
            return 1;
        }
    }

    public static async Task<int> SeedCustomerAdminAsync(
        IHost host,
        string slug,
        string adminEmail,
        string adminPassword,
        string? adminName,
        CancellationToken ct)
    {
        if (!SlugRegex.IsMatch(slug))
        {
            WriteError("Slug must match ^[a-zA-Z0-9_-]+$.");
            return 2;
        }

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var customer = await central.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Slug == slug.Trim(), ct)
                .ConfigureAwait(false);

            if (customer is null)
            {
                WriteError("Customer not found for slug.");
                return 2;
            }

            var plain = await secret.DecryptAsync(customer.EncryptedConnectionString, customer.EncryptionKeyVersion, ct)
                .ConfigureAwait(false);
            var options = new DbContextOptionsBuilder<CustomerDbContext>()
                .UseSqlServer(plain)
                .Options;

            await using var db = new CustomerDbContext(options);

            var emailNorm = adminEmail.Trim();
            var exists = await db.AppUsers.AnyAsync(u => u.Email == emailNorm, ct).ConfigureAwait(false);
            if (exists)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Admin user already exists: {emailNorm}");
                Console.ResetColor();
                return 0;
            }

            var now = DateTime.UtcNow;
            db.AppUsers.Add(new AppUser
            {
                Email = emailNorm,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
                FullName = string.IsNullOrWhiteSpace(adminName) ? "Admin" : adminName.Trim(),
                Role = UserRole.Owner,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Admin user created: {emailNorm} (Owner)");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("seed-customer-admin failed: " + ex.Message);
            return 1;
        }
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s[..(max - 1)] + "…";
    }

    private static void WriteLineStep(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static string SlugToPascalCase(string slug)
    {
        return string.Join("", slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                if (part.Length == 0) return string.Empty;
                return char.ToUpperInvariant(part[0]) + (part.Length > 1 ? part[1..].ToLowerInvariant() : string.Empty);
            }));
    }

    private static string BuildMasterConnectionString(string server, string sqlAuth)
    {
        if (string.Equals(sqlAuth, "trusted", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = "master",
                IntegratedSecurity = true,
                TrustServerCertificate = true
            }.ConnectionString;
        }

        if (sqlAuth.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = sqlAuth[4..];
            var idx = rest.IndexOf(':');
            if (idx <= 0)
                throw new InvalidOperationException("Invalid --sql-auth format. Use 'sql:username:password'.");
            var user = rest[..idx];
            var pass = rest[(idx + 1)..];
            return new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = "master",
                UserID = user,
                Password = pass,
                TrustServerCertificate = true
            }.ConnectionString;
        }

        throw new InvalidOperationException("Invalid --sql-auth. Use 'trusted' or 'sql:username:password'.");
    }

    private static string BuildCustomerConnectionString(string server, string sqlAuth, string database)
    {
        if (string.Equals(sqlAuth, "trusted", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                IntegratedSecurity = true,
                TrustServerCertificate = true
            }.ConnectionString;
        }

        if (sqlAuth.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = sqlAuth[4..];
            var idx = rest.IndexOf(':');
            if (idx <= 0)
                throw new InvalidOperationException("Invalid --sql-auth format. Use 'sql:username:password'.");
            var user = rest[..idx];
            var pass = rest[(idx + 1)..];
            return new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                UserID = user,
                Password = pass,
                TrustServerCertificate = true
            }.ConnectionString;
        }

        throw new InvalidOperationException("Invalid --sql-auth. Use 'trusted' or 'sql:username:password'.");
    }

    private static async Task EnsureDatabaseExistsAsync(string server, string sqlAuth, string dbName, CancellationToken ct)
    {
        if (!SqlDbNameRegex.IsMatch(dbName))
            throw new InvalidOperationException("Invalid database name after validation.");

        var masterCs = BuildMasterConnectionString(server, sqlAuth);
        await using var conn = new SqlConnection(masterCs);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        const string checkSql = "SELECT COUNT(*) FROM sys.databases WHERE name = @name;";
        await using (var check = new SqlCommand(checkSql, conn))
        {
            check.Parameters.AddWithValue("@name", dbName);
            var count = Convert.ToInt32(await check.ExecuteScalarAsync(ct).ConfigureAwait(false)!);
            if (count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Database '{dbName}' already exists; skipping CREATE DATABASE.");
                Console.ResetColor();
                return;
            }
        }

        // dbName validated: safe to embed as bracket-quoted identifier
        var escaped = dbName.Replace("]", "]]", StringComparison.Ordinal);
        var createSql = $"CREATE DATABASE [{escaped}]";
        await using (var create = new SqlCommand(createSql, conn))
        {
            create.CommandTimeout = 120;
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        WriteLineStep($"Database '{dbName}' created.");
    }

    private static string SanitizeMigrationError(Exception ex)
    {
        var msg = ex.Message ?? "Unknown error";
        msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
        return msg.Length <= 500 ? msg : msg[..500];
    }
}
