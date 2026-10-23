using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wasla.Application;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Email;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Onboarding;
using Wasla.Application.Setup;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using System.Text.Json;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.ReferenceData;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;

namespace Wasla.Cli;

internal static class CliCommands
{
    /// <summary>
    /// Prints a BCrypt hash (no master key required).
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

    public static async Task<int> AddCentralAdminAsync(
        IHost host,
        string email,
        string password,
        string displayName,
        CancellationToken ct)
    {
        var emailNorm = (email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(emailNorm))
        {
            WriteError("--email is required.");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            WriteError("--password is required.");
            return 2;
        }

        var normalizedEmail = emailNorm.ToUpperInvariant();
        var name = string.IsNullOrWhiteSpace(displayName) ? "Central Admin" : displayName.Trim();
        if (name.Length > 150) name = name[..150];

        try
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();

            var exists = await db.CentralAdminUsers
                .AsNoTracking()
                .AnyAsync(x => x.NormalizedEmail == normalizedEmail, ct)
                .ConfigureAwait(false);

            if (exists)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Central admin already exists for this email. No changes were made.");
                Console.ResetColor();
                return 0;
            }

            var now = DateTime.UtcNow;
            db.CentralAdminUsers.Add(new CentralAdminUser
            {
                Id = Guid.NewGuid(),
                Email = emailNorm,
                NormalizedEmail = normalizedEmail,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                DisplayName = name,
                IsActive = true,
                LastLoginAt = null,
                CreatedAt = now,
                UpdatedAt = now
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Central admin user created.");
            Console.ResetColor();
            Console.WriteLine($"Email: {emailNorm}");
            Console.WriteLine($"DisplayName: {name}");
            return 0;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
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

    public static async Task<int> ResetCentralAdminPasswordAsync(
        IHost host,
        string email,
        string password,
        CancellationToken ct)
    {
        var emailNorm = (email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(emailNorm))
        {
            WriteError("--email is required.");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            WriteError("--password is required.");
            return 2;
        }

        var normalizedEmail = emailNorm.ToUpperInvariant();

        try
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();

            var user = await db.CentralAdminUsers.FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, ct).ConfigureAwait(false);
            if (user is null)
            {
                WriteError("Central admin not found for this email.");
                return 2;
            }

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
            // Ends every existing Central Admin session of this admin.
            user.SecurityStamp = Guid.NewGuid();
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Central admin password updated.");
            Console.ResetColor();
            Console.WriteLine($"Email: {user.Email}");
            return 0;
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    public static async Task<int> ResetPasswordAsync(
        IHost host,
        string? scope,
        string? email,
        string? tenantSelector,
        bool dryRun,
        CancellationToken ct,
        ICliPasswordReader? passwordReader = null,
        Func<Tenant, CancellationToken, Task<TenantDbContext>>? openTenant = null)
    {
        using var diScope = host.Services.CreateScope();
        var central = diScope.ServiceProvider.GetRequiredService<CentralDbContext>();
        openTenant ??= (tenant, token) => CliPasswordReset.OpenTenantDatabaseAsync(
            diScope.ServiceProvider.GetRequiredService<ISecretManager>(),
            tenant,
            token);

        return await CliPasswordReset.ExecuteAsync(
            central,
            scope,
            email,
            tenantSelector,
            dryRun,
            openTenant,
            passwordReader ?? new ConsoleCliPasswordReader(),
            ct).ConfigureAwait(false);
    }

    public static async Task<int> ListCentralAdminsAsync(IHost host, CancellationToken ct)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();

            var rows = await db.CentralAdminUsers
                .AsNoTracking()
                .OrderBy(x => x.NormalizedEmail)
                .Select(x => new { x.Email, x.DisplayName, x.IsActive, x.LastLoginAt, x.CreatedAt })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            Console.WriteLine("Email | DisplayName | Active | LastLoginAt (UTC) | CreatedAt (UTC)");
            foreach (var r in rows)
            {
                Console.WriteLine($"{r.Email} | {r.DisplayName} | {(r.IsActive ? "yes" : "no")} | {r.LastLoginAt:u} | {r.CreatedAt:u}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    private static readonly Regex SlugRegex = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex SqlDbNameRegex = new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    private static bool IsProductionEnvironment() =>
        CliDestructiveGuards.IsProductionEnvironment(
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));

    /// <summary>Why this tenant's database must not be dropped or recreated (WAS-93), or null when it may.</summary>
    private static async Task<string?> GetDestructiveRefusalAsync(
        IHost host,
        CentralDbContext central,
        Tenant tenant,
        string plainConnectionString,
        CancellationToken ct)
    {
        var centralConnectionString = host.Services.GetRequiredService<IConfiguration>().GetConnectionString("CentralDb");
        var databaseName = tenant.DatabaseName;
        var references = await central.Tenants
            .AsNoTracking()
            .CountAsync(t => t.DatabaseName == databaseName, ct)
            .ConfigureAwait(false);
        return CliDestructiveGuards.GetDatabaseRefusalReason(databaseName, plainConnectionString, centralConnectionString, references);
    }

    private static bool CheckDestructiveSafety(bool confirm, bool forceProduction)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("WARNING: This will permanently delete data.");
        Console.WriteLine("This will permanently delete the tenant record and/or the tenant database.");
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

    public static async Task<int> ProvisionSignupRequestAsync(
        IHost host,
        Guid registrationId,
        bool dryRun,
        bool force,
        string? sqlServer,
        string sqlAuth,
        CancellationToken ct)
    {
        try
        {
            using var scope = host.Services.CreateScope();

            if (dryRun)
            {
                // Dry-run: show what would happen without modifying anything
                var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
                var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

                var registration = await central.PendingRegistrations
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == registrationId, ct)
                    .ConfigureAwait(false);

                if (registration is null)
                {
                    WriteError($"Pending registration not found: {registrationId}");
                    return 2;
                }

                var serverResolution = ResolveTenantSqlServer(configuration, sqlServer);
                if (!serverResolution.IsResolved)
                {
                    WriteError(serverResolution.Error!);
                    return 2;
                }

                var server = serverResolution.Server;
                var resolvedAuth = sqlAuth;

                Console.WriteLine("(dry-run) Would provision signup request:");
                Console.WriteLine($"  RegistrationId: {registration.Id}");
                Console.WriteLine($"  BusinessName:   {registration.BusinessName}");
                Console.WriteLine($"  Slug:           {registration.Slug}");
                Console.WriteLine($"  PrimaryDomain:  {registration.PrimaryDomain}");
                Console.WriteLine($"  DatabaseName:   {registration.DatabaseName.Trim()}");
                Console.WriteLine($"  OwnerEmail:     {registration.OwnerEmail}");
                Console.WriteLine($"  PlanCode:       {registration.PlanCode}");
                Console.WriteLine($"  BillingPeriod:  {registration.BillingPeriod}");
                Console.WriteLine($"  Status:         {registration.Status}");
                Console.WriteLine($"  SQL Server:     {server}");
                Console.WriteLine($"  SQL Auth:       {(resolvedAuth.StartsWith("sql:", StringComparison.OrdinalIgnoreCase) ? "sql:***" : resolvedAuth)}");
                return 0;
            }

            // Actual provisioning — delegate to the shared service
            var provisioningService = scope.ServiceProvider.GetRequiredService<IPendingRegistrationProvisioningService>();

            WriteLineStep("Provisioning signup request…");

            var result = await provisioningService.ProvisionAsync(
                registrationId,
                force: force,
                sqlServerOverride: string.IsNullOrWhiteSpace(sqlServer) ? null : sqlServer.Trim(),
                sqlAuthOverride: string.Equals(sqlAuth, "trusted", StringComparison.OrdinalIgnoreCase) ? null : sqlAuth,
                ct: ct).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case ProvisioningOutcome.Success:
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✓ Signup request provisioned successfully");
                    Console.ResetColor();
                    Console.WriteLine($"""
                        RegistrationId: {registrationId}
                        TenantId:       {result.TenantId}
                        Name:           {result.TenantName}
                        Domain:         {result.TenantDomain}
                        Next steps:
                        - Ensure DNS points {result.TenantDomain} to your server
                        - Log in at https://{result.TenantDomain}/auth/login
                        """);
                    return 0;

                case ProvisioningOutcome.AlreadyProvisioned:
                    Console.WriteLine("Already provisioned.");
                    Console.WriteLine($"TenantId: {result.TenantId}");
                    Console.WriteLine($"Domain:   {result.TenantDomain}");
                    return 0;

                case ProvisioningOutcome.NotFound:
                    WriteError($"Pending registration not found: {registrationId}");
                    return 2;

                case ProvisioningOutcome.NotEligible:
                case ProvisioningOutcome.ValidationError:
                    WriteError(result.Message ?? "Registration is not eligible for provisioning.");
                    return 2;

                case ProvisioningOutcome.Failed:
                default:
                    WriteError(result.Message ?? "Provisioning failed.");
                    return 3;
            }
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    public static async Task<int> AddTenantAsync(
        IHost host,
        string name,
        string slug,
        string domain,
        string adminEmail,
        string adminPassword,
        string adminName,
        string? sqlServer,
        string sqlAuth,
        TenantContactUpdate contact,
        CancellationToken ct)
    {
        if (!SlugRegex.IsMatch(slug))
        {
            WriteError("Slug must match ^[a-zA-Z0-9_-]+$.");
            return 2;
        }

        var contactError = ValidateContact(contact);
        if (contactError is not null)
        {
            WriteError(contactError);
            return 2;
        }

        // Server-assigned and random (WAS-93): the slug is only a readable label, so no slug can name CentralDb,
        // a system database or another tenant's database.
        var dbName = TenantDatabaseNames.Generate(slug);
        var nameError = TenantDatabaseNames.GetRejectionReason(
            dbName,
            CentralCatalogOrNull(host.Services.GetRequiredService<IConfiguration>().GetConnectionString("CentralDb")));
        if (nameError is not null)
        {
            WriteError(nameError);
            return 2;
        }

        // An explicitly configured SQL Server only; stops before any CentralDb or SQL Server work (WAS-93).
        var serverResolution = ResolveTenantSqlServer(host.Services.GetRequiredService<IConfiguration>(), sqlServer);
        if (!serverResolution.IsResolved)
        {
            WriteError(serverResolution.Error!);
            return 2;
        }

        var server = serverResolution.Server!;

        var dbCreated = false;
        Tenant? insertedCentral = null;

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var exists = await central.Tenants
                .AsNoTracking()
                .AnyAsync(c => c.Slug == slug || c.PrimaryDomain == domain, ct)
                .ConfigureAwait(false);
            if (exists)
            {
                WriteError("A tenant with this slug or primary domain already exists.");
                return 2;
            }

            var databaseNameTaken = await central.Tenants.AsNoTracking().AnyAsync(c => c.DatabaseName == dbName, ct).ConfigureAwait(false)
                || await central.PendingRegistrations.AsNoTracking().AnyAsync(p => p.DatabaseName == dbName, ct).ConfigureAwait(false);
            if (databaseNameTaken)
            {
                WriteError($"Database name '{dbName}' is already recorded in CentralDb. Run the command again for a new name.");
                return 2;
            }

            WriteLineStep($"Target database name: {dbName}");

            // Creates and marks the database for this tenant id; an existing database of that name is never adopted.
            var tenantId = Guid.NewGuid();
            await SqlServerTenantDatabases
                .ClaimAsync(server, sqlAuth, dbName, TenantDatabaseOwnership.ForTenant(tenantId), ct)
                .ConfigureAwait(false);
            WriteLineStep($"Database '{dbName}' created.");
            dbCreated = true;

            var tenantConnString = BuildTenantConnectionString(server, sqlAuth, dbName);
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(tenantConnString)
                .Options;
            var migrationNow = DateTime.UtcNow;
            string migrationResult;
            try
            {
                await using var db = new TenantDbContext(options);
                WriteLineStep("Applying TenantDb migrations…");
                await db.Database.MigrateAsync(ct).ConfigureAwait(false);
                migrationResult = "Success";
            }
            catch (Exception ex)
            {
                migrationResult = "Failed: " + SanitizeMigrationError(ex);
                throw;
            }

            WriteLineStep("Encrypting connection string…");
            var (encrypted, keyVersion) = await secret.EncryptAsync(tenantConnString, ct).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            var tenant = new Tenant
            {
                Id = tenantId,
                Name = name,
                Slug = slug,
                PrimaryDomain = domain,
                DatabaseName = dbName,
                EncryptedConnectionString = encrypted,
                EncryptionKeyVersion = keyVersion,
                SchemaVersion = TenantDbSchemaVersions.Current,
                LastMigrationAt = migrationNow,
                LastMigrationResult = migrationResult,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };

            // The tenant row and its membership are stored together or not at all.
            await using (var transaction = await central.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                central.Tenants.Add(tenant);
                await central.SaveChangesAsync(ct).ConfigureAwait(false);

                // Paid signup creates this row too; the restaurant setup step reads the contact fields from it.
                WriteLineStep("Recording tenant membership…");
                await TenantMembershipRecords.EnsureAsync(
                    central,
                    tenantId,
                    CliMembershipSeed(adminEmail, contact),
                    now,
                    ct).ConfigureAwait(false);

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            insertedCentral = tenant;

            await using (var userDb = new TenantDbContext(options))
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

                // A new restaurant starts in Setup until a user completes or skips guided setup.
                await TenantOperationalModes.EnsureNewTenantStartsInSetupAsync(userDb, now, ct).ConfigureAwait(false);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Tenant created successfully");
            Console.ResetColor();
            Console.WriteLine($"""
                Id:       {tenantId}
                Name:     {name}
                Domain:   {domain}
                Database: {dbName}
                Admin:    {adminEmail}  (password set)
                Next steps:
                - Ensure DNS points {domain} to your server
                - Log in at https://{domain}/auth/login
                """);
            WarnIfContactIncomplete(name, contact.BusinessPhone, contact.City, contact.Country, slug);

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
                        ? $"Warning: partial failure. Drop database [{dbName}] and delete CentralDb tenant row Id={insertedCentral.Id} if you need to retry clean."
                        : "Warning: database was created or modified. If registration failed partway, manually drop the database and remove the Tenant row from CentralDb if it was inserted.");
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

    public static async Task<int> SeedTurkeyReferenceDataAsync(IHost host, CancellationToken ct)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<TurkeyReferenceDataSeeder>>();
            var seeder = new TurkeyReferenceDataSeeder(central, logger);

            var result = await seeder.SeedAsync(ct).ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(
                $"✓ seed-turkey-reference-data completed. Cities inserted={result.CitiesInserted}, districts inserted={result.DistrictsInserted}, total cities={result.TotalCities}.");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            WriteError($"seed-turkey-reference-data failed: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> SeedAddressReferenceDataAsync(IHost host, CancellationToken ct)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var turkeyLogger = scope.ServiceProvider.GetRequiredService<ILogger<TurkeyReferenceDataSeeder>>();
            var importerLogger = scope.ServiceProvider.GetRequiredService<ILogger<AddressReferenceDataImporter>>();
            var turkeySeeder = new TurkeyReferenceDataSeeder(central, turkeyLogger);
            var importer = new AddressReferenceDataImporter(central, turkeySeeder, importerLogger);

            var result = await importer.ImportAsync(ct).ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(
                $"✓ seed-address-reference-data completed. Countries inserted={result.CountriesInserted}, cities inserted={result.CitiesInserted}, districts inserted={result.DistrictsInserted}, neighborhoods inserted={result.NeighborhoodsInserted}, streets inserted={result.StreetsInserted}, total cities={result.TotalCities}.");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            WriteError($"seed-address-reference-data failed: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> MigrateTenantAsync(
        IHost host,
        string? slug,
        string? tenantIdArg,
        CancellationToken ct)
    {
        var hasSlug = !string.IsNullOrWhiteSpace(slug);
        if (hasSlug && !string.IsNullOrWhiteSpace(tenantIdArg))
        {
            WriteError("Use either --slug or --tenant-id, not both.");
            return 2;
        }
        if (!hasSlug && string.IsNullOrWhiteSpace(tenantIdArg))
        {
            WriteError("Specify --slug or --tenant-id.");
            return 2;
        }
        if (!string.IsNullOrWhiteSpace(slug) && !SlugRegex.IsMatch(slug))
        {
            WriteError("Slug must match ^[a-zA-Z0-9_-]+$.");
            return 2;
        }

        Guid? tenantId = null;
        if (!string.IsNullOrWhiteSpace(tenantIdArg))
        {
            if (!Guid.TryParse(tenantIdArg, out var g))
            {
                WriteError("--tenant-id must be a valid GUID.");
                return 2;
            }
            tenantId = g;
        }

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var slugNorm = slug?.Trim() ?? string.Empty;
            var tenant = tenantId is { } id
                ? await central.Tenants.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
                : await central.Tenants.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slugNorm, ct).ConfigureAwait(false);

            if (tenant is null)
            {
                WriteError("Tenant not found.");
                return 2;
            }
            if (!tenant.IsActive)
            {
                WriteError("Tenant is inactive.");
                return 2;
            }

            WriteLineStep($"Tenant: {tenant.Name}  slug={tenant.Slug}  database={tenant.DatabaseName}");

            var plain = await secret.DecryptAsync(tenant.EncryptedConnectionString, tenant.EncryptionKeyVersion, ct)
                .ConfigureAwait(false);
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(plain)
                .Options;

            await using (var db = new TenantDbContext(options))
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
                Console.WriteLine($"Pending TenantDb migrations: {pending.Count}");
                foreach (var m in pending)
                    Console.WriteLine($"  {m}");

                WriteLineStep("Applying TenantDb migrations…");
                await db.Database.MigrateAsync(ct).ConfigureAwait(false);

                var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
                var latest = applied.Count > 0 ? applied[^1] : "(none)";
                Console.WriteLine($"Latest applied TenantDb migration: {latest}");
            }

            var tracked = await central.Tenants.FirstOrDefaultAsync(c => c.Id == tenant.Id, ct).ConfigureAwait(false);
            if (tracked is not null)
            {
                var when = DateTime.UtcNow;
                tracked.LastMigrationAt = when;
                tracked.LastMigrationResult = "Success";
                tracked.SchemaVersion = TenantDbSchemaVersions.Current;
                tracked.UpdatedAt = when;
                await central.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ migrate-tenant completed successfully.");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                using var scope2 = host.Services.CreateScope();
                var central2 = scope2.ServiceProvider.GetRequiredService<CentralDbContext>();
                Guid? idForTrack = tenantId;
                if (idForTrack is null && !string.IsNullOrWhiteSpace(slug))
                {
                    var slugN = slug.Trim();
                    var c2 = await central2.Tenants.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Slug == slugN, ct)
                        .ConfigureAwait(false);
                    idForTrack = c2?.Id;
                }
                if (idForTrack is { } trackId)
                {
                    var tracked = await central2.Tenants.FirstOrDefaultAsync(c => c.Id == trackId, ct).ConfigureAwait(false);
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

            WriteError($"migrate-tenant failed: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> MigrateAllTenantsAsync(
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

            var query = central.Tenants.AsNoTracking().Where(c => c.IsActive);
            var list = (await query.ToListAsync(ct).ConfigureAwait(false)).OrderBy(c => c.Slug).ToList();

            if (onlySlugs is { Length: > 0 })
            {
                var set = onlySlugs.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                list = list.Where(c => set.Contains(c.Slug)).ToList();
                foreach (var s in set)
                {
                    if (list.All(c => !string.Equals(c.Slug, s, StringComparison.OrdinalIgnoreCase)))
                    {
                        WriteError($"Tenant slug not found: {s}");
                        return 2;
                    }
                }
            }

            var total = list.Count;
            Console.WriteLine($"Active tenants to process: {total}");
            foreach (var c in list)
                Console.WriteLine($"  - {c.Name}  ({c.Slug})  [{c.DatabaseName}]");

            if (dryRun)
            {
                foreach (var c in list)
                {
                    try
                    {
                        var plain = await secret.DecryptAsync(c.EncryptedConnectionString, c.EncryptionKeyVersion, ct).ConfigureAwait(false);
                        var options = new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(plain).Options;
                        await using var db = new TenantDbContext(options);
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
                    var options = new DbContextOptionsBuilder<TenantDbContext>()
                        .UseSqlServer(plain)
                        .Options;
                    await using var db = new TenantDbContext(options);

                    await db.Database.MigrateAsync(ct).ConfigureAwait(false);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✓ {c.Slug}  ({c.DatabaseName})");
                    Console.ResetColor();

                    var tracked = await central.Tenants.FirstOrDefaultAsync(x => x.Id == c.Id, ct).ConfigureAwait(false);
                    if (tracked is not null)
                    {
                        var when = DateTime.UtcNow;
                        tracked.LastMigrationAt = when;
                        tracked.LastMigrationResult = "Success";
                        tracked.SchemaVersion = TenantDbSchemaVersions.Current;
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
                        var tracked = await central.Tenants.FirstOrDefaultAsync(x => x.Id == c.Id, ct).ConfigureAwait(false);
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
                Console.WriteLine("Failed tenants (slug / name):");
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

    public static async Task<int> MigrationStatusAsync(IHost host, bool requireCurrent, CancellationToken ct)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();
            var notCurrent = false;

            Console.WriteLine("=== CentralDb ===");
            var centralPending = (await central.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            var centralApplied = (await central.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            var centralKnown = central.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            var centralUnknown = centralApplied.Any(m => !centralKnown.Contains(m));
            var centralLatest = centralApplied.Count > 0 ? centralApplied[^1] : "(none)";
            Console.WriteLine($"Latest applied: {centralLatest}");
            Console.WriteLine($"Pending count:  {centralPending.Count}");
            if (centralPending.Count > 0 || centralUnknown)
            {
                notCurrent = true;
                if (centralUnknown)
                    Console.WriteLine("CentralDb has migrations unknown to this build.");
            }
            if (centralPending.Count > 0)
            {
                foreach (var m in centralPending)
                    Console.WriteLine($"  {m}");
            }

            var tenants = await central.Tenants
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Slug)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("=== Tenant databases (active) ===");
            foreach (var c in tenants)
            {
                Console.WriteLine();
                Console.WriteLine($"{c.Name}  |  slug: {c.Slug}  |  db: {c.DatabaseName}");
                Console.WriteLine(
                    $"  CentralDb tracking: LastMigrationAt={c.LastMigrationAt:u}  Result={c.LastMigrationResult ?? "(null)"}  SchemaVersion={c.SchemaVersion}");
                try
                {
                    var plain = await secret.DecryptAsync(c.EncryptedConnectionString, c.EncryptionKeyVersion, ct).ConfigureAwait(false);
                    var options = new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(plain).Options;
                    await using var db = new TenantDbContext(options);
                    var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
                    var latest = applied.Count > 0 ? applied[^1] : "(none)";
                    var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
                    var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
                    var unknown = applied.Any(m => !known.Contains(m));
                    Console.WriteLine($"  TenantDb latest applied: {latest}");
                    Console.WriteLine($"  Pending: {pending.Count}");
                    if (pending.Count > 0 || unknown)
                    {
                        notCurrent = true;
                        if (unknown)
                            Console.WriteLine("  TenantDb has migrations unknown to this build.");
                    }
                    if (pending.Count > 0)
                    {
                        foreach (var m in pending)
                            Console.WriteLine($"    {m}");
                    }
                }
                catch (Exception ex)
                {
                    notCurrent = true;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(requireCurrent
                        ? $"  (could not read migration status) {ex.GetType().Name}"
                        : $"  (could not read migration status) {ex.Message}");
                    Console.ResetColor();
                }
            }

            if (requireCurrent)
                Console.WriteLine(notCurrent ? "Migration readiness check failed." : "All active databases are current for this build.");
            return requireCurrent && notCurrent ? 1 : 0;
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    public static async Task<int> ListTenantsAsync(IHost host, CancellationToken ct)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var rows = await central.Tenants
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
        string tenantSlug,
        string email,
        string password,
        string role,
        string? fullName,
        CancellationToken ct)
    {
        if (!SlugRegex.IsMatch(tenantSlug))
        {
            WriteError("Tenant slug must match ^[a-zA-Z0-9_-]+$.");
            return 2;
        }

        if (!Enum.TryParse<UserRole>(role, true, out var userRole) ||
            userRole is not (UserRole.Owner or UserRole.Manager or UserRole.Kitchen or UserRole.Cashier or UserRole.Viewer))
        {
            WriteError("Role must be one of: Owner, Manager, Kitchen, Cashier, Viewer.");
            return 2;
        }

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var tenant = await central.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Slug == tenantSlug, ct)
                .ConfigureAwait(false);
            if (tenant is null)
            {
                WriteError("Tenant not found for slug.");
                return 2;
            }
            if (!tenant.IsActive)
            {
                WriteError("Tenant is inactive.");
                return 2;
            }

            var plain = await secret.DecryptAsync(tenant.EncryptedConnectionString, tenant.EncryptionKeyVersion, ct)
                .ConfigureAwait(false);
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(plain)
                .Options;
            await using var db = new TenantDbContext(options);

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

    public static async Task<int> DeleteTenantAsync(
        IHost host,
        string? slug,
        string? tenantIdArg,
        bool confirm,
        bool forceProduction,
        CancellationToken ct)
    {
        if (!CheckDestructiveSafety(confirm, forceProduction))
            return 2;

        var hasSlug = !string.IsNullOrWhiteSpace(slug);
        if (hasSlug && !string.IsNullOrWhiteSpace(tenantIdArg))
        {
            WriteError("Use either --slug or --tenant-id, not both.");
            return 2;
        }
        if (!hasSlug && string.IsNullOrWhiteSpace(tenantIdArg))
        {
            WriteError("Specify --slug or --tenant-id.");
            return 2;
        }
        if (hasSlug && !SlugRegex.IsMatch(slug!))
        {
            WriteError("Slug must match ^[a-zA-Z0-9_-]+$.");
            return 2;
        }

        Guid? tenantId = null;
        if (!string.IsNullOrWhiteSpace(tenantIdArg))
        {
            if (!Guid.TryParse(tenantIdArg, out var g))
            {
                WriteError("--tenant-id must be a valid GUID.");
                return 2;
            }
            tenantId = g;
        }

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var slugNorm = slug?.Trim() ?? string.Empty;
            var tenant = tenantId is { } id
                ? await central.Tenants.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
                : await central.Tenants.FirstOrDefaultAsync(c => c.Slug == slugNorm, ct).ConfigureAwait(false);

            if (tenant is null)
            {
                WriteError("Tenant not found.");
                return 2;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"About to delete tenant '{tenant.Name}' (slug={tenant.Slug}) and drop database '{tenant.DatabaseName}'.");
            Console.ResetColor();

            var plain = await secret.DecryptAsync(tenant.EncryptedConnectionString, tenant.EncryptionKeyVersion, ct)
                .ConfigureAwait(false);

            var refusal = await GetDestructiveRefusalAsync(host, central, tenant, plain, ct).ConfigureAwait(false);
            if (refusal is not null)
            {
                WriteError("Refusing to drop the database: " + refusal);
                return 2;
            }
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(plain)
                .Options;

            await using (var db = new TenantDbContext(options))
            {
                WriteLineStep("Dropping TenantDb (if exists)…");
                await db.Database.EnsureDeletedAsync(ct).ConfigureAwait(false);
            }

            WriteLineStep("Deleting CentralDb tenant record…");
            central.Tenants.Remove(tenant);
            await central.SaveChangesAsync(ct).ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Tenant deleted successfully.");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("delete-tenant failed: " + ex.Message);
            return 1;
        }
    }

    public static async Task<int> ResetTenantDbAsync(
        IHost host,
        string? slug,
        string? tenantIdArg,
        bool confirm,
        bool forceProduction,
        CancellationToken ct)
    {
        if (!CheckDestructiveSafety(confirm, forceProduction))
            return 2;

        var hasSlug = !string.IsNullOrWhiteSpace(slug);
        if (hasSlug && !string.IsNullOrWhiteSpace(tenantIdArg))
        {
            WriteError("Use either --slug or --tenant-id, not both.");
            return 2;
        }
        if (!hasSlug && string.IsNullOrWhiteSpace(tenantIdArg))
        {
            WriteError("Specify --slug or --tenant-id.");
            return 2;
        }
        if (hasSlug && !SlugRegex.IsMatch(slug!))
        {
            WriteError("Slug must match ^[a-zA-Z0-9_-]+$.");
            return 2;
        }

        Guid? tenantId = null;
        if (!string.IsNullOrWhiteSpace(tenantIdArg))
        {
            if (!Guid.TryParse(tenantIdArg, out var g))
            {
                WriteError("--tenant-id must be a valid GUID.");
                return 2;
            }
            tenantId = g;
        }

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();

            var slugNorm = slug?.Trim() ?? string.Empty;
            var tenant = tenantId is { } id
                ? await central.Tenants.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
                : await central.Tenants.FirstOrDefaultAsync(c => c.Slug == slugNorm, ct).ConfigureAwait(false);

            if (tenant is null)
            {
                WriteError("Tenant not found.");
                return 2;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"About to RESET database '{tenant.DatabaseName}' for tenant '{tenant.Name}' (slug={tenant.Slug}).");
            Console.ResetColor();

            var plain = await secret.DecryptAsync(tenant.EncryptedConnectionString, tenant.EncryptionKeyVersion, ct)
                .ConfigureAwait(false);

            var refusal = await GetDestructiveRefusalAsync(host, central, tenant, plain, ct).ConfigureAwait(false);
            if (refusal is not null)
            {
                WriteError("Refusing to reset the database: " + refusal);
                return 2;
            }
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(plain)
                .Options;

            var migrationNow = DateTime.UtcNow;
            var migrationResult = "Unknown";

            try
            {
                await using var db = new TenantDbContext(options);
                WriteLineStep("Dropping TenantDb (if exists)…");
                await db.Database.EnsureDeletedAsync(ct).ConfigureAwait(false);

                WriteLineStep("Applying TenantDb migrations…");
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
                var tracked = await central.Tenants.FirstOrDefaultAsync(x => x.Id == tenant.Id, ct).ConfigureAwait(false);
                if (tracked is not null)
                {
                    tracked.LastMigrationAt = migrationNow;
                    tracked.LastMigrationResult = migrationResult;
                    tracked.SchemaVersion = TenantDbSchemaVersions.Current;
                    tracked.UpdatedAt = DateTime.UtcNow;
                    await central.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Tenant database reset successfully (schema migrated).");
            Console.ResetColor();
            Console.WriteLine("Note: All users/orders/platform connections/branches were deleted. Recreate admin user if needed (seed-tenant-admin).");
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("reset-tenant-db failed: " + ex.Message);
            return 1;
        }
    }

    public static async Task<int> ResetAllTenantDbsAsync(
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

            var tenants = await central.Tenants
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Slug)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var total = tenants.Count;
            var succeeded = 0;
            var failed = new List<(string Slug, string Name)>();

            Console.WriteLine($"Active tenants to reset: {total}");

            foreach (var c in tenants)
            {
                try
                {
                    var plain = await secret.DecryptAsync(c.EncryptedConnectionString, c.EncryptionKeyVersion, ct).ConfigureAwait(false);

                    // Refused tenants are reported as failed and left untouched; the others still run.
                    var refusal = await GetDestructiveRefusalAsync(host, central, c, plain, ct).ConfigureAwait(false);
                    if (refusal is not null)
                        throw new InvalidOperationException("Refused: " + refusal);

                    var options = new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(plain).Options;

                    var migrationNow = DateTime.UtcNow;
                    var migrationResult = "Unknown";

                    try
                    {
                        await using var db = new TenantDbContext(options);
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
                        var tracked = await central.Tenants.FirstOrDefaultAsync(x => x.Id == c.Id, ct).ConfigureAwait(false);
                        if (tracked is not null)
                        {
                            tracked.LastMigrationAt = migrationNow;
                            tracked.LastMigrationResult = migrationResult;
                            tracked.SchemaVersion = TenantDbSchemaVersions.Current;
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
                Console.WriteLine("Failed tenants (slug / name):");
                foreach (var f in failed)
                    Console.WriteLine($"  - {f.Slug}  /  {f.Name}");
                Console.ResetColor();
            }

            return failed.Count > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            WriteError("reset-all-tenant-dbs failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Sets the restaurant contact the setup checklist requires, e.g. for a tenant created by
    /// add-tenant without it. Creates the tenant's membership row when missing. Repeating the
    /// same values changes nothing; omitted fields keep their stored value.
    /// </summary>
    public static async Task<int> UpdateTenantProfileAsync(
        IHost host,
        string? tenantSelector,
        TenantContactUpdate contact,
        CancellationToken ct)
    {
        var selector = (tenantSelector ?? string.Empty).Trim();
        if (selector.Length == 0)
        {
            WriteError("Requires --tenant <slug-or-id>.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(contact.BusinessPhone)
            && string.IsNullOrWhiteSpace(contact.City)
            && string.IsNullOrWhiteSpace(contact.Country))
        {
            WriteError("Provide at least one of --business-phone, --city or --country.");
            return 2;
        }

        var contactError = ValidateContact(contact);
        if (contactError is not null)
        {
            WriteError(contactError);
            return 2;
        }

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();

            var tenant = Guid.TryParse(selector, out var tenantId)
                ? await central.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct).ConfigureAwait(false)
                : await central.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == selector, ct).ConfigureAwait(false);
            if (tenant is null)
            {
                WriteError("Tenant not found.");
                return 2;
            }

            await TenantMembershipRecords.UpdateContactAsync(
                central,
                tenant.Id,
                contact,
                CliMembershipSeed(ownerEmail: null, new TenantContactUpdate(null, null, null)),
                DateTime.UtcNow,
                ct).ConfigureAwait(false);

            var stored = await central.TenantMemberships
                .AsNoTracking()
                .Where(m => m.TenantId == tenant.Id)
                .Select(m => new { m.BusinessPhone, m.City, m.Country })
                .SingleAsync(ct)
                .ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Restaurant profile updated for {tenant.Slug}");
            Console.ResetColor();
            Console.WriteLine($"  Business phone: {(string.IsNullOrWhiteSpace(stored.BusinessPhone) ? "not set" : "set")}");
            Console.WriteLine($"  City:           {stored.City ?? "not set"}");
            Console.WriteLine($"  Country:        {stored.Country ?? "not set"}");
            WarnIfContactIncomplete(tenant.Name, stored.BusinessPhone, stored.City, stored.Country, tenant.Slug);
            return 0;
        }
        catch (Exception ex) when (ex is SqlException or DbUpdateException)
        {
            WriteError("Database operation failed. No profile change was saved.");
            return 3;
        }
    }

    /// <summary>
    /// Membership for a tenant created by an operator rather than paid signup: no trial, default plan.
    /// </summary>
    internal static TenantMembershipSeed CliMembershipSeed(string? ownerEmail, TenantContactUpdate contact) => new(
        PlanCode: WaslaPlanCodes.Starter,
        BillingPeriod: "Monthly",
        Status: MembershipStatus.Active,
        TrialEndsAt: null,
        OwnerEmail: NullIfBlank(ownerEmail),
        BusinessPhone: NullIfBlank(contact.BusinessPhone),
        City: NullIfBlank(contact.City),
        Country: NullIfBlank(contact.Country),
        BusinessType: null);

    internal static string? ValidateContact(TenantContactUpdate contact)
    {
        if ((contact.BusinessPhone?.Trim().Length ?? 0) > TenantMembershipRecords.BusinessPhoneMaxLength)
            return $"--business-phone must be at most {TenantMembershipRecords.BusinessPhoneMaxLength} characters.";
        if ((contact.City?.Trim().Length ?? 0) > TenantMembershipRecords.LocationMaxLength)
            return $"--city must be at most {TenantMembershipRecords.LocationMaxLength} characters.";
        if ((contact.Country?.Trim().Length ?? 0) > TenantMembershipRecords.LocationMaxLength)
            return $"--country must be at most {TenantMembershipRecords.LocationMaxLength} characters.";
        return null;
    }

    private static void WarnIfContactIncomplete(string? name, string? phone, string? city, string? country, string slug)
    {
        if (TenantSetupReadiness.IsRestaurantComplete(new RestaurantProfileFacts(true, name, phone, city, country)))
            return;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Note: the restaurant setup step stays incomplete until a business phone and a city or country are set:");
        Console.WriteLine($"  update-tenant-profile --tenant {slug} --business-phone <phone> --city <city> [--country <country>]");
        Console.ResetColor();
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static async Task<int> SeedTenantAdminAsync(
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

            var tenant = await central.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Slug == slug.Trim(), ct)
                .ConfigureAwait(false);

            if (tenant is null)
            {
                WriteError("Tenant not found for slug.");
                return 2;
            }

            var plain = await secret.DecryptAsync(tenant.EncryptedConnectionString, tenant.EncryptionKeyVersion, ct)
                .ConfigureAwait(false);
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(plain)
                .Options;

            await using var db = new TenantDbContext(options);

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
            WriteError("seed-tenant-admin failed: " + ex.Message);
            return 1;
        }
    }

    private static async Task TrySendPanelReadyEmailAsync(
        IServiceScope scope,
        PendingRegistration registration,
        CancellationToken ct)
    {
        try
        {
            var factory = scope.ServiceProvider.GetRequiredService<ProvisioningEmailFactory>();
            var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
            var message = factory.BuildPanelReadyEmail(registration);
            await sender.SendAsync(message, ct).ConfigureAwait(false);

            Console.WriteLine("PanelReady notification email prepared.");
        }
        catch (Exception ex)
        {
            var logger = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Wasla.Cli.ProvisionSignupRequest");
            logger?.LogWarning(
                ex,
                "PanelReady email failed for registration {RegistrationId}",
                registration.Id);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Provisioning completed, but notification email failed.");
            Console.WriteLine(ex.Message);
            Console.ResetColor();
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

    private static string BuildTenantConnectionString(string server, string sqlAuth, string database)
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

    private static string? ValidateProvisioningRequiredFields(PendingRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.BusinessName))
            return "BusinessName is required.";
        if (string.IsNullOrWhiteSpace(registration.Slug))
            return "Slug is required.";
        if (!SlugRegex.IsMatch(registration.Slug.Trim()))
            return "Slug must match ^[a-zA-Z0-9_-]+$.";
        if (string.IsNullOrWhiteSpace(registration.PrimaryDomain))
            return "PrimaryDomain is required.";
        if (string.IsNullOrWhiteSpace(registration.DatabaseName))
            return "DatabaseName is required.";
        if (!SqlDbNameRegex.IsMatch(registration.DatabaseName.Trim()))
            return $"DatabaseName '{registration.DatabaseName}' is invalid.";
        if (string.IsNullOrWhiteSpace(registration.OwnerEmail))
            return "OwnerEmail is required.";
        if (string.IsNullOrWhiteSpace(registration.OwnerFullName))
            return "OwnerFullName is required.";
        if (string.IsNullOrWhiteSpace(registration.PasswordHash))
            return "PasswordHash is required.";
        if (string.IsNullOrWhiteSpace(registration.PlanCode))
            return "PlanCode is required.";
        if (string.IsNullOrWhiteSpace(registration.BillingPeriod))
            return "BillingPeriod is required.";
        return null;
    }

    private static async Task<string?> ValidateProvisioningUniquenessAsync(
        CentralDbContext central,
        PendingRegistration registration,
        CancellationToken ct)
    {
        var slug = registration.Slug.Trim();
        var domain = registration.PrimaryDomain.Trim();
        var databaseName = registration.DatabaseName.Trim();

        var tenantBySlug = await central.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug, ct)
            .ConfigureAwait(false);
        if (tenantBySlug is not null && tenantBySlug.Id != registration.TenantId)
            return $"A tenant with slug '{slug}' already exists (TenantId={tenantBySlug.Id}). Investigate manually.";

        var tenantByDomain = await central.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.PrimaryDomain == domain, ct)
            .ConfigureAwait(false);
        if (tenantByDomain is not null && tenantByDomain.Id != registration.TenantId)
            return $"A tenant with primary domain '{domain}' already exists (TenantId={tenantByDomain.Id}). Investigate manually.";

        var tenantByDb = await central.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.DatabaseName == databaseName, ct)
            .ConfigureAwait(false);
        if (tenantByDb is not null && tenantByDb.Id != registration.TenantId)
            return $"A tenant with database name '{databaseName}' already exists (TenantId={tenantByDb.Id}). Investigate manually.";

        var now = DateTime.UtcNow;
        var pendingConflict = await central.PendingRegistrations
            .AsNoTracking()
            .AnyAsync(
                p => p.Id != registration.Id
                     && (p.Status == PendingRegistrationStatus.Draft
                         || p.Status == PendingRegistrationStatus.AwaitingPayment
                         || p.Status == PendingRegistrationStatus.PaymentSucceeded)
                     && (p.ExpiresAtUtc == null || p.ExpiresAtUtc > now)
                     && (p.Slug == slug || p.PrimaryDomain == domain || p.DatabaseName == databaseName),
                ct)
            .ConfigureAwait(false);
        if (pendingConflict)
            return "Another active pending registration reserves the same slug, domain, or database name.";

        return null;
    }

    private static TenantSqlServerResolution ResolveTenantSqlServer(IConfiguration configuration, string? sqlServerOverride) =>
        TenantSqlServerConfiguration.Resolve(sqlServerOverride, configuration[TenantSqlServerConfiguration.ServerInstanceKey]);

    private static string? CentralCatalogOrNull(string? centralConnectionString)
    {
        if (string.IsNullOrWhiteSpace(centralConnectionString))
            return null;

        try
        {
            return new SqlConnectionStringBuilder(centralConnectionString).InitialCatalog;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string SanitizeMigrationError(Exception ex)
    {
        var msg = ex.Message ?? "Unknown error";
        msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
        return msg.Length <= 500 ? msg : msg[..500];
    }

    public static async Task<int> GeneratePrintBridgeTokenAsync(
        IHost host,
        Guid? tenantId,
        string? slug,
        string? deviceName,
        CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var centralDb = scope.ServiceProvider.GetRequiredService<CentralDbContext>();

        Guid resolvedTenantId;
        if (tenantId.HasValue && tenantId.Value != Guid.Empty)
        {
            resolvedTenantId = tenantId.Value;
        }
        else if (!string.IsNullOrWhiteSpace(slug))
        {
            var tenant = await centralDb.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Slug == slug.Trim(), ct)
                .ConfigureAwait(false);
            if (tenant is null)
            {
                WriteError($"Tenant not found for slug '{slug}'.");
                return 2;
            }

            resolvedTenantId = tenant.Id;
        }
        else
        {
            WriteError("Usage: generate-print-bridge-token --tenant-id <guid> | --slug <slug> [--name <device-name>]");
            return 2;
        }

        var tenantActive = await centralDb.Tenants
            .AsNoTracking()
            .AnyAsync(c => c.Id == resolvedTenantId && c.IsActive, ct)
            .ConfigureAwait(false);

        if (!tenantActive)
        {
            WriteError("Tenant not found or inactive.");
            return 2;
        }

        var name = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName.Trim();
        if (name.Length > 200) name = name[..200];

        var rawToken = PrintBridgeTokenHasher.GenerateRawToken();
        var tokenHash = PrintBridgeTokenHasher.HashToken(rawToken);
        var now = DateTime.UtcNow;

        var device = new PrintBridgeDevice
        {
            TenantId = resolvedTenantId,
            Name = name,
            TokenHash = tokenHash,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        centralDb.PrintBridgeDevices.Add(device);
        await centralDb.SaveChangesAsync(ct).ConfigureAwait(false);

        Console.WriteLine("Print Bridge device registered.");
        Console.WriteLine($"DeviceId: {device.Id}");
        Console.WriteLine($"DeviceName: {device.Name}");
        Console.WriteLine("Raw token (shown once — copy now):");
        Console.WriteLine(rawToken);
        return 0;
    }

    /// <summary>Development helper: creates a pending receipt PrintJob for Print Bridge testing.</summary>
    public static async Task<int> SeedPrintJobAsync(
        IHost host,
        string? slug,
        string? tenantIdArg,
        CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(host, slug, tenantIdArg, ct).ConfigureAwait(false);
        if (tenant is null) return 2;

        await using var db = await OpenTenantDbAsync(host, tenant, ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var externalOrderId = $"TEST-{now:yyyyMMddHHmmssfff}";
        var order = new Order
        {
            Platform = FoodPlatform.Yemeksepeti,
            ExternalOrderId = externalOrderId,
            ExternalOrderCode = "TEST-001",
            IdempotencyKey = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"Yemeksepeti:{externalOrderId}"))),
            InternalStatus = OrderStatus.Accepted,
            PlatformStatus = "Accepted",
            CustomerName = "Test Customer",
            CustomerPhone = "+905551112233",
            CustomerAddress = "Test Address 1",
            TotalAmount = 125.50m,
            ReceivedAt = now,
            CreatedAtPlatform = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        order.Items.Add(new OrderItem
        {
            ProductName = "Test Burger",
            Quantity = 2,
            UnitPrice = 50m,
            TotalPrice = 100m,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        WriteLineStep($"Created test order {order.Id}");

        var payload = JsonSerializer.Serialize(new
        {
            tenantDisplayName = tenant.Name,
            platform = order.Platform.ToString(),
            externalOrderCode = order.ExternalOrderCode,
            receivedAtUtc = order.ReceivedAt,
            customerName = order.CustomerName,
            customerPhone = order.CustomerPhone,
            deliveryAddress = order.CustomerAddress,
            totalAmount = order.TotalAmount,
            paymentMethod = "Cash",
            items = order.Items.Select(i => new
            {
                productName = i.ProductName,
                quantity = i.Quantity,
                unitPrice = i.UnitPrice,
                lineTotal = i.TotalPrice
            }).ToList()
        });

        var job = new PrintJob
        {
            OrderId = order.Id,
            Type = PrintJobType.Receipt,
            Status = PrintJobStatus.Pending,
            CopyCount = 1,
            PayloadJson = payload,
            AttemptCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            // Development helper: no active-job check or lock, but the version still starts above the order's jobs.
            StatusVersion = await Wasla.Infrastructure.Realtime.PrintJobStatusVersions
                .NextForNewJobAsync(db, order.Id, ct)
                .ConfigureAwait(false)
        };

        db.PrintJobs.Add(job);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        Console.WriteLine($"Pending PrintJob created. JobId={job.Id}, OrderId={order.Id}");
        return 0;
    }

    public static async Task<int> ListPrintJobsAsync(
        IHost host,
        string? slug,
        string? tenantIdArg,
        CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(host, slug, tenantIdArg, ct).ConfigureAwait(false);
        if (tenant is null) return 2;

        await using var db = await OpenTenantDbAsync(host, tenant, ct).ConfigureAwait(false);

        var jobs = await db.PrintJobs.AsNoTracking()
            .OrderByDescending(j => j.CreatedAt)
            .Take(20)
            .Select(j => new
            {
                j.Id,
                j.OrderId,
                Status = j.Status.ToString(),
                j.AttemptCount,
                j.LockedBy,
                j.ErrorMessage,
                j.LockedAt,
                j.LastAttemptAt,
                j.PrintedAt,
                j.CreatedAt
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (jobs.Count == 0)
        {
            Console.WriteLine("No PrintJobs found.");
            return 0;
        }

        foreach (var j in jobs)
        {
            Console.WriteLine(
                $"{j.Id}  Status={j.Status}  Attempts={j.AttemptCount}  LockedBy={j.LockedBy ?? "-"}  Error={j.ErrorMessage ?? "-"}  PrintedAt={j.PrintedAt?.ToString("u") ?? "-"}");
        }

        return 0;
    }

    private static async Task<Tenant?> ResolveTenantAsync(
        IHost host,
        string? slug,
        string? tenantIdArg,
        CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();

        if (!string.IsNullOrWhiteSpace(tenantIdArg))
        {
            if (!Guid.TryParse(tenantIdArg, out var id))
            {
                WriteError("--tenant-id must be a valid GUID.");
                return null;
            }

            return await central.Tenants.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.IsActive, ct)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            WriteError("Specify --slug or --tenant-id.");
            return null;
        }

        return await central.Tenants.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slug.Trim() && c.IsActive, ct)
            .ConfigureAwait(false);
    }

    private static async Task<TenantDbContext> OpenTenantDbAsync(IHost host, Tenant tenant, CancellationToken ct)
    {
        var secret = host.Services.GetRequiredService<ISecretManager>();
        var plain = await secret.DecryptAsync(tenant.EncryptedConnectionString, tenant.EncryptionKeyVersion, ct)
            .ConfigureAwait(false);
        var options = new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(plain).Options;
        return new TenantDbContext(options);
    }
}
