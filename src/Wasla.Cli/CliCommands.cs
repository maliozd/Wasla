using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Email;
using Wasla.Application.Abstractions.Security;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using System.Text.Json;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.ReferenceData;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Persistence.Tenant;

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

    public static async Task<int> ProvisionSignupRequestAsync(
        IHost host,
        Guid registrationId,
        bool dryRun,
        bool force,
        string? sqlServer,
        string sqlAuth,
        CancellationToken ct)
    {
        var dbCreated = false;
        Tenant? insertedCentral = null;
        string? dbName = null;

        try
        {
            using var scope = host.Services.CreateScope();
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretManager>();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

            var registration = await central.PendingRegistrations
                .FirstOrDefaultAsync(r => r.Id == registrationId, ct)
                .ConfigureAwait(false);

            if (registration is null)
            {
                WriteError($"Pending registration not found: {registrationId}");
                return 2;
            }

            if (registration.Status == PendingRegistrationStatus.Provisioned)
            {
                if (registration.TenantId is { } existingTenantId)
                {
                    var existingTenant = await central.Tenants
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => t.Id == existingTenantId, ct)
                        .ConfigureAwait(false);
                    if (existingTenant is not null)
                    {
                        Console.WriteLine("Already provisioned.");
                        Console.WriteLine($"TenantId: {existingTenant.Id}");
                        Console.WriteLine($"Domain:   {existingTenant.PrimaryDomain}");
                        return 0;
                    }
                }

                if (!force)
                {
                    WriteError(
                        "Registration is marked Provisioned but the tenant record is missing. Use --force to retry provisioning.");
                    return 2;
                }
            }
            else if (registration.Status == PendingRegistrationStatus.PaymentSucceeded
                     && registration.TenantId is { } linkedTenantId)
            {
                var linkedTenant = await central.Tenants
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == linkedTenantId, ct)
                    .ConfigureAwait(false);
                if (linkedTenant is not null)
                {
                    registration.Status = PendingRegistrationStatus.Provisioned;
                    registration.ProvisionedAtUtc ??= DateTime.UtcNow;
                    await central.SaveChangesAsync(ct).ConfigureAwait(false);
                    Console.WriteLine("Already provisioned.");
                    Console.WriteLine($"TenantId: {linkedTenant.Id}");
                    Console.WriteLine($"Domain:   {linkedTenant.PrimaryDomain}");
                    return 0;
                }
            }
            else if (registration.Status != PendingRegistrationStatus.PaymentSucceeded)
            {
                WriteError("Registration must be PaymentSucceeded before provisioning.");
                return 2;
            }

            var fieldError = ValidateProvisioningRequiredFields(registration);
            if (fieldError is not null)
            {
                WriteError(fieldError);
                return 2;
            }

            dbName = registration.DatabaseName.Trim();

            var uniquenessError = await ValidateProvisioningUniquenessAsync(
                central,
                registration,
                ct).ConfigureAwait(false);
            if (uniquenessError is not null)
            {
                WriteError(uniquenessError);
                return 2;
            }

            var server = string.IsNullOrWhiteSpace(sqlServer)
                ? configuration["CustomerDb:ServerInstance"]?.Trim()
                  ?? configuration.GetSection("OrderHub:CustomerOnboarding")["ServerInstance"]?.Trim()
                  ?? "."
                : sqlServer.Trim();

            if (dryRun)
            {
                Console.WriteLine("(dry-run) Would provision signup request:");
                Console.WriteLine($"  RegistrationId: {registration.Id}");
                Console.WriteLine($"  BusinessName:   {registration.BusinessName}");
                Console.WriteLine($"  Slug:           {registration.Slug}");
                Console.WriteLine($"  PrimaryDomain:  {registration.PrimaryDomain}");
                Console.WriteLine($"  DatabaseName:   {dbName}");
                Console.WriteLine($"  OwnerEmail:     {registration.OwnerEmail}");
                Console.WriteLine($"  PlanCode:       {registration.PlanCode}");
                Console.WriteLine($"  BillingPeriod:  {registration.BillingPeriod}");
                Console.WriteLine($"  SQL Server:     {server}");
                Console.WriteLine($"  SQL Auth:       {(sqlAuth.StartsWith("sql:", StringComparison.OrdinalIgnoreCase) ? "sql:***" : sqlAuth)}");
                return 0;
            }

            var dbAlreadyExists = await DatabaseExistsAsync(server, sqlAuth, dbName, ct).ConfigureAwait(false);
            if (dbAlreadyExists && registration.TenantId is null && !force)
            {
                WriteError(
                    $"Tenant database '{dbName}' already exists but no tenant is linked to this registration. Use --force to retry provisioning.");
                return 2;
            }

            WriteLineStep($"Target database name: {dbName}");

            await EnsureDatabaseExistsAsync(server, sqlAuth, dbName, ct).ConfigureAwait(false);
            dbCreated = !dbAlreadyExists;

            var customerConnString = BuildCustomerConnectionString(server, sqlAuth, dbName);
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(customerConnString)
                .Options;

            var migrationNow = DateTime.UtcNow;
            string migrationResult;
            try
            {
                await using var db = new TenantDbContext(options);
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

            var tenantId = registration.TenantId ?? Guid.NewGuid();
            var now = DateTime.UtcNow;
            var trialDays = configuration.GetSection("OrderHub:CustomerOnboarding").GetValue("TrialDays", 14);

            var tenant = new Tenant
            {
                Id = tenantId,
                Name = registration.BusinessName.Trim(),
                Slug = registration.Slug.Trim(),
                PrimaryDomain = registration.PrimaryDomain.Trim(),
                DatabaseName = dbName,
                EncryptedConnectionString = encrypted,
                EncryptionKeyVersion = keyVersion,
                SchemaVersion = MigrationMetadata.CurrentCustomerDbSchemaVersion,
                LastMigrationAt = migrationNow,
                LastMigrationResult = migrationResult,
                IsActive = true,
                BillingPaymentStatus = TenantBillingPaymentStatus.Paid,
                ProvisioningStatus = ProvisioningStatus.Completed,
                SubscriptionStatus = SubscriptionStatus.Trialing,
                CreatedAt = now,
                UpdatedAt = now
            };

            central.Tenants.Add(tenant);
            await central.SaveChangesAsync(ct).ConfigureAwait(false);
            insertedCentral = tenant;

            var membershipExists = await central.TenantMemberships
                .AnyAsync(m => m.TenantId == tenantId, ct)
                .ConfigureAwait(false);
            if (!membershipExists)
            {
                central.TenantMemberships.Add(new TenantMembership
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PlanCode = registration.PlanCode.Trim(),
                    BillingPeriod = registration.BillingPeriod.Trim(),
                    Status = MembershipStatus.Trial,
                    StartedAt = now,
                    TrialEndsAt = now.AddDays(trialDays),
                    OwnerEmail = registration.OwnerEmail.Trim(),
                    BusinessPhone = registration.BusinessPhone.Trim(),
                    City = registration.City.Trim(),
                    Country = registration.Country.Trim(),
                    BusinessType = registration.BusinessType,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                await central.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            await using (var userDb = new TenantDbContext(options))
            {
                var ownerEmail = registration.OwnerEmail.Trim();
                var ownerExists = await userDb.AppUsers
                    .AnyAsync(u => u.Email == ownerEmail, ct)
                    .ConfigureAwait(false);
                if (!ownerExists)
                {
                    WriteLineStep("Creating owner admin user…");
                    userDb.AppUsers.Add(new AppUser
                    {
                        Email = ownerEmail,
                        PasswordHash = registration.PasswordHash,
                        FullName = registration.OwnerFullName.Trim(),
                        Role = UserRole.Owner,
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                    await userDb.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Owner user already exists in tenant database: {ownerEmail}");
                    Console.ResetColor();
                }
            }

            registration.Status = PendingRegistrationStatus.Provisioned;
            registration.TenantId = tenantId;
            registration.ProvisionedAtUtc = now;
            await central.SaveChangesAsync(ct).ConfigureAwait(false);

            await TrySendPanelReadyEmailAsync(scope, registration, ct).ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Signup request provisioned successfully");
            Console.ResetColor();
            Console.WriteLine($"""
                RegistrationId: {registration.Id}
                TenantId:       {tenantId}
                Name:           {tenant.Name}
                Domain:         {tenant.PrimaryDomain}
                Database:       {dbName}
                Admin:          {registration.OwnerEmail}
                Next steps:
                - Ensure DNS points {tenant.PrimaryDomain} to your server
                - Log in at https://{tenant.PrimaryDomain}/auth/login
                """);

            return 0;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or DbUpdateException)
        {
            WriteError(ex.Message);
            if (dbCreated || insertedCentral is not null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                if (insertedCentral is not null)
                {
                    Console.WriteLine(
                        $"Warning: partial failure. Pending registration was not marked Provisioned. Tenant row Id={insertedCentral.Id} may exist in CentralDb.");
                }
                if (dbName is not null)
                {
                    Console.WriteLine(
                        insertedCentral is not null
                            ? $"If you need to retry clean, drop database [{dbName}] and delete CentralDb tenant row Id={insertedCentral.Id}."
                            : $"Warning: database [{dbName}] was created or modified. Drop it and remove any partial CentralDb tenant row before retrying.");
                }
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
        var dbName = $"Wasla_{pascal}";
        if (!SqlDbNameRegex.IsMatch(dbName))
        {
            WriteError($"Derived database name '{dbName}' is invalid.");
            return 2;
        }

        var server = string.IsNullOrWhiteSpace(sqlServer)
            ? host.Services.GetRequiredService<IConfiguration>()["CustomerDb:ServerInstance"]?.Trim() ?? "."
            : sqlServer.Trim();

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
                WriteError("A customer with this slug or primary domain already exists.");
                return 2;
            }

            WriteLineStep($"Target database name: {dbName}");

            await EnsureDatabaseExistsAsync(server, sqlAuth, dbName, ct).ConfigureAwait(false);
            dbCreated = true;

            var customerConnString = BuildCustomerConnectionString(server, sqlAuth, dbName);
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(customerConnString)
                .Options;
            var migrationNow = DateTime.UtcNow;
            string migrationResult;
            try
            {
                await using var db = new TenantDbContext(options);
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
            var customer = new Tenant
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

            central.Tenants.Add(customer);
            await central.SaveChangesAsync(ct).ConfigureAwait(false);
            insertedCentral = customer;

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
                ? await central.Tenants.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
                : await central.Tenants.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slugNorm, ct).ConfigureAwait(false);

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
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(plain)
                .Options;

            await using (var db = new TenantDbContext(options))
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

            var tracked = await central.Tenants.FirstOrDefaultAsync(c => c.Id == customer.Id, ct).ConfigureAwait(false);
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

            var customers = await central.Tenants
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
                    var options = new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(plain).Options;
                    await using var db = new TenantDbContext(options);
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

            var customer = await central.Tenants
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
                ? await central.Tenants.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
                : await central.Tenants.FirstOrDefaultAsync(c => c.Slug == slugNorm, ct).ConfigureAwait(false);

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
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(plain)
                .Options;

            await using (var db = new TenantDbContext(options))
            {
                WriteLineStep("Dropping CustomerDb (if exists)…");
                await db.Database.EnsureDeletedAsync(ct).ConfigureAwait(false);
            }

            WriteLineStep("Deleting CentralDb customer record…");
            central.Tenants.Remove(customer);
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
                ? await central.Tenants.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
                : await central.Tenants.FirstOrDefaultAsync(c => c.Slug == slugNorm, ct).ConfigureAwait(false);

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
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(plain)
                .Options;

            var migrationNow = DateTime.UtcNow;
            var migrationResult = "Unknown";

            try
            {
                await using var db = new TenantDbContext(options);
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
                var tracked = await central.Tenants.FirstOrDefaultAsync(x => x.Id == customer.Id, ct).ConfigureAwait(false);
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

            var customers = await central.Tenants
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

            var customer = await central.Tenants
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
            WriteError("seed-customer-admin failed: " + ex.Message);
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

    private static async Task<bool> DatabaseExistsAsync(string server, string sqlAuth, string dbName, CancellationToken ct)
    {
        var masterCs = BuildMasterConnectionString(server, sqlAuth);
        await using var conn = new SqlConnection(masterCs);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        const string checkSql = "SELECT COUNT(*) FROM sys.databases WHERE name = @name;";
        await using var check = new SqlCommand(checkSql, conn);
        check.Parameters.AddWithValue("@name", dbName);
        return Convert.ToInt32(await check.ExecuteScalarAsync(ct).ConfigureAwait(false)!) > 0;
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

    public static async Task<int> GeneratePrintBridgeTokenAsync(
        IHost host,
        Guid? customerId,
        string? slug,
        string? deviceName,
        CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var centralDb = scope.ServiceProvider.GetRequiredService<CentralDbContext>();

        Guid resolvedCustomerId;
        if (customerId.HasValue && customerId.Value != Guid.Empty)
        {
            resolvedCustomerId = customerId.Value;
        }
        else if (!string.IsNullOrWhiteSpace(slug))
        {
            var customer = await centralDb.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Slug == slug.Trim(), ct)
                .ConfigureAwait(false);
            if (customer is null)
            {
                WriteError($"Customer not found for slug '{slug}'.");
                return 2;
            }

            resolvedCustomerId = customer.Id;
        }
        else
        {
            WriteError("Usage: generate-print-bridge-token --customer-id <guid> | --slug <slug> [--name <device-name>]");
            return 2;
        }

        var customerActive = await centralDb.Tenants
            .AsNoTracking()
            .AnyAsync(c => c.Id == resolvedCustomerId && c.IsActive, ct)
            .ConfigureAwait(false);

        if (!customerActive)
        {
            WriteError("Customer not found or inactive.");
            return 2;
        }

        var name = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName.Trim();
        if (name.Length > 200) name = name[..200];

        var rawToken = PrintBridgeTokenHasher.GenerateRawToken();
        var tokenHash = PrintBridgeTokenHasher.HashToken(rawToken);
        var now = DateTime.UtcNow;

        var device = new PrintBridgeDevice
        {
            TenantId = resolvedCustomerId,
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
        string? customerIdArg,
        CancellationToken ct)
    {
        var customer = await ResolveCustomerAsync(host, slug, customerIdArg, ct).ConfigureAwait(false);
        if (customer is null) return 2;

        await using var db = await OpenCustomerDbAsync(host, customer, ct).ConfigureAwait(false);

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
            tenantDisplayName = customer.Name,
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
            UpdatedAt = now
        };

        db.PrintJobs.Add(job);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        Console.WriteLine($"Pending PrintJob created. JobId={job.Id}, OrderId={order.Id}");
        return 0;
    }

    public static async Task<int> ListPrintJobsAsync(
        IHost host,
        string? slug,
        string? customerIdArg,
        CancellationToken ct)
    {
        var customer = await ResolveCustomerAsync(host, slug, customerIdArg, ct).ConfigureAwait(false);
        if (customer is null) return 2;

        await using var db = await OpenCustomerDbAsync(host, customer, ct).ConfigureAwait(false);

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

    private static async Task<Tenant?> ResolveCustomerAsync(
        IHost host,
        string? slug,
        string? customerIdArg,
        CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();

        if (!string.IsNullOrWhiteSpace(customerIdArg))
        {
            if (!Guid.TryParse(customerIdArg, out var id))
            {
                WriteError("--customer-id must be a valid GUID.");
                return null;
            }

            return await central.Tenants.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.IsActive, ct)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            WriteError("Specify --slug or --customer-id.");
            return null;
        }

        return await central.Tenants.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slug.Trim() && c.IsActive, ct)
            .ConfigureAwait(false);
    }

    private static async Task<TenantDbContext> OpenCustomerDbAsync(IHost host, Tenant customer, CancellationToken ct)
    {
        var secret = host.Services.GetRequiredService<ISecretManager>();
        var plain = await secret.DecryptAsync(customer.EncryptedConnectionString, customer.EncryptionKeyVersion, ct)
            .ConfigureAwait(false);
        var options = new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(plain).Options;
        return new TenantDbContext(options);
    }
}
