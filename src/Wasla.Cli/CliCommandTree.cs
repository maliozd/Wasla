using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Hosting;
using Wasla.Infrastructure.Services;

namespace Wasla.Cli;

/// <summary>
/// The registered command tree. <c>Program.cs</c> handles help, unknown commands and the master key before it invokes
/// this tree; the handlers use the host only when they run.
/// </summary>
internal static class CliCommandTree
{
    public static RootCommand Build(IHost host)
    {
        // --- provision-signup-request ---
        var provisionSignupRequest = new Command(
            "provision-signup-request",
            "Provision a paid PendingRegistration into a live tenant (DB + central registry + owner user).");

        var optRegistrationId = new Option<Guid>("--registration-id", "PendingRegistration id (GUID)") { IsRequired = true };
        var optPsrDryRun = new Option<bool>("--dry-run", "Validate only; do not create database, tenant, or user");
        var optPsrForce = new Option<bool>("--force", "Retry a registration marked Provisioned without a tenant row. Never adopts a database Wasla did not create for this registration");
        var optPsrSqlServer = new Option<string?>("--sql-server", "SQL Server instance for the tenant database; required unless TenantDb:ServerInstance is configured (no default)");
        var optPsrSqlAuth = new Option<string>("--sql-auth", () => "trusted", "Trusted Windows auth, or 'sql:username:password'");

        provisionSignupRequest.AddOption(optRegistrationId);
        provisionSignupRequest.AddOption(optPsrDryRun);
        provisionSignupRequest.AddOption(optPsrForce);
        provisionSignupRequest.AddOption(optPsrSqlServer);
        provisionSignupRequest.AddOption(optPsrSqlAuth);

        provisionSignupRequest.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.ProvisionSignupRequestAsync(
                host,
                p.GetValueForOption(optRegistrationId),
                p.GetValueForOption(optPsrDryRun),
                p.GetValueForOption(optPsrForce),
                p.GetValueForOption(optPsrSqlServer),
                p.GetValueForOption(optPsrSqlAuth) ?? "trusted",
                context.GetCancellationToken());
        });

        // --- add-tenant ---
        var addTenant = new Command("add-tenant", "Onboard a new tenant (DB + central registry + first admin).");

        var optName = new Option<string>("--name", "Display name") { IsRequired = true };
        var optSlug = new Option<string>("--slug", "URL-friendly slug, e.g. ahmet-pizza") { IsRequired = true };
        var optDomain = new Option<string>("--domain", "Primary domain, e.g. ahmet.Wasla.com") { IsRequired = true };
        var optAdminEmail = new Option<string>("--admin-email", "First admin user email") { IsRequired = true };
        var optAdminPassword = new Option<string>("--admin-password", "First admin password (visible in shell history)") { IsRequired = true };
        var optAdminName = new Option<string>("--admin-name", () => "Admin", "Admin full name");
        var optSqlServer = new Option<string?>("--sql-server", "SQL Server instance for the tenant database; required unless TenantDb:ServerInstance is configured (no default)");
        var optSqlAuth = new Option<string>("--sql-auth", () => "trusted", "Trusted Windows auth, or 'sql:username:password'");
        var optBusinessPhone = new Option<string?>("--business-phone", "Restaurant business phone (required by the setup checklist)");
        var optCity = new Option<string?>("--city", "Restaurant city (the setup checklist needs a city or country)");
        var optCountry = new Option<string?>("--country", "Restaurant country (the setup checklist needs a city or country)");

        addTenant.AddOption(optName);
        addTenant.AddOption(optSlug);
        addTenant.AddOption(optDomain);
        addTenant.AddOption(optAdminEmail);
        addTenant.AddOption(optAdminPassword);
        addTenant.AddOption(optAdminName);
        addTenant.AddOption(optSqlServer);
        addTenant.AddOption(optSqlAuth);
        addTenant.AddOption(optBusinessPhone);
        addTenant.AddOption(optCity);
        addTenant.AddOption(optCountry);

        addTenant.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            var name = p.GetValueForOption(optName)!;
            var slug = p.GetValueForOption(optSlug)!;
            var domain = p.GetValueForOption(optDomain)!;
            var adminEmail = p.GetValueForOption(optAdminEmail)!;
            var adminPassword = p.GetValueForOption(optAdminPassword)!;
            var adminName = p.GetValueForOption(optAdminName) ?? "Admin";
            var sqlServer = p.GetValueForOption(optSqlServer);
            var sqlAuth = p.GetValueForOption(optSqlAuth) ?? "trusted";
            var ct = context.GetCancellationToken();
            var contact = new TenantContactUpdate(
                p.GetValueForOption(optBusinessPhone),
                p.GetValueForOption(optCity),
                p.GetValueForOption(optCountry));
            context.ExitCode = await CliCommands.AddTenantAsync(
                host, name, slug, domain, adminEmail, adminPassword, adminName, sqlServer, sqlAuth, contact, ct);
        });

        // --- update-tenant-profile ---
        var updateTenantProfile = new Command(
            "update-tenant-profile",
            "Set the restaurant business phone, city or country used by the setup checklist (idempotent).");
        var optProfileTenant = new Option<string>("--tenant", "Tenant slug or id in CentralDb") { IsRequired = true };
        var optProfilePhone = new Option<string?>("--business-phone", "Restaurant business phone");
        var optProfileCity = new Option<string?>("--city", "Restaurant city");
        var optProfileCountry = new Option<string?>("--country", "Restaurant country");
        updateTenantProfile.AddOption(optProfileTenant);
        updateTenantProfile.AddOption(optProfilePhone);
        updateTenantProfile.AddOption(optProfileCity);
        updateTenantProfile.AddOption(optProfileCountry);
        updateTenantProfile.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.UpdateTenantProfileAsync(
                host,
                p.GetValueForOption(optProfileTenant),
                new TenantContactUpdate(
                    p.GetValueForOption(optProfilePhone),
                    p.GetValueForOption(optProfileCity),
                    p.GetValueForOption(optProfileCountry)),
                context.GetCancellationToken());
        });

        // --- migrate-central ---
        var migrateCentral = new Command("migrate-central", "Apply pending EF Core migrations for CentralDb (tenant registry).");

        migrateCentral.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await CliCommands.MigrateCentralAsync(host, context.GetCancellationToken());
        });

        // --- seed-turkey-reference-data ---
        var seedTurkeyReferenceData = new Command(
            "seed-turkey-reference-data",
            "Seed all 81 Turkish cities and districts into CentralDb (idempotent).");

        seedTurkeyReferenceData.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await CliCommands.SeedTurkeyReferenceDataAsync(host, context.GetCancellationToken());
        });

        // --- seed-address-reference-data ---
        var seedAddressReferenceData = new Command(
            "seed-address-reference-data",
            "Import address reference data into CentralDb (idempotent; neighborhoods/streets when data files are available).");

        seedAddressReferenceData.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await CliCommands.SeedAddressReferenceDataAsync(host, context.GetCancellationToken());
        });

        // --- migrate-tenant ---
        var migrateTenant = new Command("migrate-tenant", "Apply pending TenantDb migrations for a single tenant.");

        var optMcSlug = new Option<string?>("--slug", "Tenant slug in CentralDb");
        var optMcTenantId = new Option<string?>("--tenant-id", "Tenant id (GUID) in CentralDb");
        migrateTenant.AddOption(optMcSlug);
        migrateTenant.AddOption(optMcTenantId);

        migrateTenant.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.MigrateTenantAsync(
                host,
                p.GetValueForOption(optMcSlug),
                p.GetValueForOption(optMcTenantId),
                context.GetCancellationToken());
        });

        // --- migration-status ---
        var migrationStatus = new Command("migration-status", "Show CentralDb and per-tenant TenantDb migration status.");
        var optRequireCurrent = new Option<bool>("--require-current", "Exit non-zero if CentralDb or any active TenantDb is pending, ahead of this build, or unreadable");
        migrationStatus.AddOption(optRequireCurrent);

        migrationStatus.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await CliCommands.MigrationStatusAsync(
                host,
                context.ParseResult.GetValueForOption(optRequireCurrent),
                context.GetCancellationToken());
        });

        // --- migrate-all-tenants ---
        var migrateAll = new Command("migrate-all-tenants", "Apply pending TenantDb migrations to every (or selected) active tenant database.");

        var optDryRun = new Option<bool>("--dry-run", "List pending migrations only, do not apply");
        var optOnly = new Option<string[]>("--only", "Migrate only these tenant slugs (repeatable)")
        {
            Arity = ArgumentArity.ZeroOrMore
        };

        migrateAll.AddOption(optDryRun);
        migrateAll.AddOption(optOnly);

        migrateAll.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            var dry = p.GetValueForOption(optDryRun);
            var only = p.GetValueForOption(optOnly);
            var ct = context.GetCancellationToken();
            context.ExitCode = await CliCommands.MigrateAllTenantsAsync(host, dry, only, ct);
        });

        // --- list-tenants ---
        var listTenants = new Command("list-tenants", "List active tenants from CentralDb.");

        listTenants.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await CliCommands.ListTenantsAsync(host, context.GetCancellationToken());
        });

        // --- add-central-admin ---
        var addCentralAdmin = new Command("add-central-admin", "Create a CentralDb central admin user (no tenant secrets).");
        var optCaEmail = new Option<string>("--email", "Admin email") { IsRequired = true };
        var optCaPassword = new Option<string>("--password", "Plaintext password (visible in history)") { IsRequired = true };
        var optCaDisplayName = new Option<string>("--display-name", () => "Central Admin", "Display name");
        addCentralAdmin.AddOption(optCaEmail);
        addCentralAdmin.AddOption(optCaPassword);
        addCentralAdmin.AddOption(optCaDisplayName);
        addCentralAdmin.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.AddCentralAdminAsync(
                host,
                p.GetValueForOption(optCaEmail)!,
                p.GetValueForOption(optCaPassword)!,
                p.GetValueForOption(optCaDisplayName) ?? "Central Admin",
                context.GetCancellationToken());
        });

        // --- reset-central-admin-password ---
        var resetCentralAdminPassword = new Command("reset-central-admin-password", "Reset password for a CentralDb central admin user.");
        var optRcaEmail = new Option<string>("--email", "Admin email") { IsRequired = true };
        var optRcaPassword = new Option<string>("--password", "New plaintext password (visible in history)") { IsRequired = true };
        resetCentralAdminPassword.AddOption(optRcaEmail);
        resetCentralAdminPassword.AddOption(optRcaPassword);
        resetCentralAdminPassword.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.ResetCentralAdminPasswordAsync(
                host,
                p.GetValueForOption(optRcaEmail)!,
                p.GetValueForOption(optRcaPassword)!,
                context.GetCancellationToken());
        });

        // --- reset-password ---
        var resetPassword = new Command("reset-password", "Reset an existing central or tenant user password.")
        {
            TreatUnmatchedTokensAsErrors = true
        };
        var optResetScope = new Option<string>("--scope", "central or tenant") { IsRequired = true };
        var optResetEmail = new Option<string>("--email", "Existing user email") { IsRequired = true };
        var optResetTenant = new Option<string?>("--tenant", "Tenant slug or tenant id. Required for --scope tenant.");
        var optResetDryRun = new Option<bool>("--dry-run", "Resolve the account only; do not prompt or write");
        resetPassword.AddOption(optResetScope);
        resetPassword.AddOption(optResetEmail);
        resetPassword.AddOption(optResetTenant);
        resetPassword.AddOption(optResetDryRun);
        resetPassword.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.ResetPasswordAsync(
                host,
                p.GetValueForOption(optResetScope),
                p.GetValueForOption(optResetEmail),
                p.GetValueForOption(optResetTenant),
                p.GetValueForOption(optResetDryRun),
                context.GetCancellationToken());
        });

        // --- list-central-admins ---
        var listCentralAdmins = new Command("list-central-admins", "List central admin users from CentralDb (safe fields only).");
        listCentralAdmins.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await CliCommands.ListCentralAdminsAsync(host, context.GetCancellationToken());
        });

        // --- encrypt ---
        var encrypt = new Command("encrypt", "Encrypt a plaintext string with the current master key.")
        {
            TreatUnmatchedTokensAsErrors = true
        };

        var argPlain = new Argument<string>("plaintext", "String to encrypt");
        encrypt.AddArgument(argPlain);

        encrypt.SetHandler(async (InvocationContext context) =>
        {
            var plain = context.ParseResult.GetValueForArgument(argPlain);
            context.ExitCode = await CliCommands.EncryptAsync(host, plain, context.GetCancellationToken());
        });

        // --- create-user ---
        var createUser = new Command("create-user", "Add a user to an existing tenant's database.");

        var optTenantSlug = new Option<string>("--tenant-slug", "Tenant slug in CentralDb") { IsRequired = true };
        var optEmail = new Option<string>("--email", "User email") { IsRequired = true };
        var optPassword = new Option<string>("--password", "Plaintext password (visible in history)") { IsRequired = true };
        var optRole = new Option<string>("--role", "Owner, Manager, Kitchen, Cashier, or Viewer") { IsRequired = true };
        var optFullName = new Option<string?>("--name", "User full name");

        createUser.AddOption(optTenantSlug);
        createUser.AddOption(optEmail);
        createUser.AddOption(optPassword);
        createUser.AddOption(optRole);
        createUser.AddOption(optFullName);

        createUser.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.CreateUserAsync(
                host,
                p.GetValueForOption(optTenantSlug)!,
                p.GetValueForOption(optEmail)!,
                p.GetValueForOption(optPassword)!,
                p.GetValueForOption(optRole)!,
                p.GetValueForOption(optFullName),
                context.GetCancellationToken());
        });

        // --- delete-tenant ---
        var deleteTenant = new Command("delete-tenant", "Permanently delete a tenant record and drop its TenantDb (DESTRUCTIVE).");
        var optDelSlug = new Option<string?>("--slug", "Tenant slug in CentralDb");
        var optDelTenantId = new Option<string?>("--tenant-id", "Tenant id (GUID) in CentralDb");
        var optConfirm = new Option<bool>("--confirm", "Required. Confirms you understand this is destructive.");
        var optForceProd = new Option<bool>("--force-production", "Allow running in Production (still requires --confirm).");
        deleteTenant.AddOption(optDelSlug);
        deleteTenant.AddOption(optDelTenantId);
        deleteTenant.AddOption(optConfirm);
        deleteTenant.AddOption(optForceProd);
        deleteTenant.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.DeleteTenantAsync(
                host,
                p.GetValueForOption(optDelSlug),
                p.GetValueForOption(optDelTenantId),
                p.GetValueForOption(optConfirm),
                p.GetValueForOption(optForceProd),
                context.GetCancellationToken());
        });

        // --- reset-tenant-db ---
        var resetTenantDb = new Command("reset-tenant-db", "Drop + recreate + migrate a single TenantDb, keeping CentralDb tenant record (DESTRUCTIVE).");
        var optResetSlug = new Option<string?>("--slug", "Tenant slug in CentralDb");
        var optResetTenantId = new Option<string?>("--tenant-id", "Tenant id (GUID) in CentralDb");
        resetTenantDb.AddOption(optResetSlug);
        resetTenantDb.AddOption(optResetTenantId);
        resetTenantDb.AddOption(optConfirm);
        resetTenantDb.AddOption(optForceProd);
        resetTenantDb.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.ResetTenantDbAsync(
                host,
                p.GetValueForOption(optResetSlug),
                p.GetValueForOption(optResetTenantId),
                p.GetValueForOption(optConfirm),
                p.GetValueForOption(optForceProd),
                context.GetCancellationToken());
        });

        // --- reset-all-tenant-dbs ---
        var resetAllTenantDbs = new Command("reset-all-tenant-dbs", "Drop + recreate + migrate ALL active TenantDbs (DESTRUCTIVE).");
        resetAllTenantDbs.AddOption(optConfirm);
        resetAllTenantDbs.AddOption(optForceProd);
        resetAllTenantDbs.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.ResetAllTenantDbsAsync(
                host,
                p.GetValueForOption(optConfirm),
                p.GetValueForOption(optForceProd),
                context.GetCancellationToken());
        });

        // --- seed-tenant-admin ---
        var seedTenantAdmin = new Command("seed-tenant-admin", "Create an Owner admin user in an existing tenant DB if missing.");
        var optSeedSlug = new Option<string>("--slug", "Tenant slug in CentralDb") { IsRequired = true };
        var optSeedEmail = new Option<string>("--admin-email", "Admin email") { IsRequired = true };
        var optSeedPassword = new Option<string>("--admin-password", "Admin password (visible in history)") { IsRequired = true };
        var optSeedName = new Option<string?>("--admin-name", "Admin full name");
        seedTenantAdmin.AddOption(optSeedSlug);
        seedTenantAdmin.AddOption(optSeedEmail);
        seedTenantAdmin.AddOption(optSeedPassword);
        seedTenantAdmin.AddOption(optSeedName);
        seedTenantAdmin.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.SeedTenantAdminAsync(
                host,
                p.GetValueForOption(optSeedSlug)!,
                p.GetValueForOption(optSeedEmail)!,
                p.GetValueForOption(optSeedPassword)!,
                p.GetValueForOption(optSeedName),
                context.GetCancellationToken());
        });

        // --- generate-print-bridge-token ---
        var generatePrintBridgeToken = new Command("generate-print-bridge-token", "Register a Print Bridge device and print a one-time agent token.");
        var optPbTenantId = new Option<Guid?>("--tenant-id", "Tenant id in CentralDb");
        var optPbSlug = new Option<string?>("--slug", "Tenant slug in CentralDb");
        var optPbDeviceName = new Option<string?>("--name", "Print Bridge device display name");
        generatePrintBridgeToken.AddOption(optPbTenantId);
        generatePrintBridgeToken.AddOption(optPbSlug);
        generatePrintBridgeToken.AddOption(optPbDeviceName);
        generatePrintBridgeToken.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.GeneratePrintBridgeTokenAsync(
                host,
                p.GetValueForOption(optPbTenantId),
                p.GetValueForOption(optPbSlug),
                p.GetValueForOption(optPbDeviceName),
                context.GetCancellationToken());
        });

        // --- seed-print-job (dev helper) ---
        var seedPrintJob = new Command("seed-print-job", "Create a pending receipt PrintJob for Print Bridge testing.");
        var optSpjSlug = new Option<string?>("--slug", "Tenant slug in CentralDb");
        var optSpjTenantId = new Option<string?>("--tenant-id", "Tenant id in CentralDb");
        seedPrintJob.AddOption(optSpjSlug);
        seedPrintJob.AddOption(optSpjTenantId);
        seedPrintJob.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.SeedPrintJobAsync(
                host,
                p.GetValueForOption(optSpjSlug),
                p.GetValueForOption(optSpjTenantId),
                context.GetCancellationToken());
        });

        // --- list-print-jobs ---
        var listPrintJobs = new Command("list-print-jobs", "List recent PrintJobs for a tenant (dev/testing).");
        listPrintJobs.AddOption(optSpjSlug);
        listPrintJobs.AddOption(optSpjTenantId);
        listPrintJobs.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliCommands.ListPrintJobsAsync(
                host,
                p.GetValueForOption(optSpjSlug),
                p.GetValueForOption(optSpjTenantId),
                context.GetCancellationToken());
        });

        // --- meal-webhook-integrator (Trendyol GO Meal webhook stage pilot) ---
        var mealWebhookIntegrator = new Command(
            "meal-webhook-integrator",
            "Create, show, enable or disable the Trendyol GO Meal webhook stage pilot's integrator, or request a webhook test order.");
        var optMwiAction = new Option<string>("--action", "create, show, enable, disable or test-order") { IsRequired = true };
        var optMwiName = new Option<string>("--name", "Integrator name (alphanumeric, unique at Trendyol GO)") { IsRequired = true };
        var optMwiBaseUrl = new Option<string?>("--base-url", "create only: the https base URL Trendyol GO posts events under");
        var optMwiTokenFile = new Option<string>("--token-file", "File that holds the integrator token (written by create, read by the others)") { IsRequired = true };
        mealWebhookIntegrator.AddOption(optMwiAction);
        mealWebhookIntegrator.AddOption(optMwiName);
        mealWebhookIntegrator.AddOption(optMwiBaseUrl);
        mealWebhookIntegrator.AddOption(optMwiTokenFile);
        mealWebhookIntegrator.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliMealWebhookCommands.IntegratorAsync(
                host,
                p.GetValueForOption(optMwiAction)!,
                p.GetValueForOption(optMwiName)!,
                p.GetValueForOption(optMwiBaseUrl),
                p.GetValueForOption(optMwiTokenFile)!,
                context.GetCancellationToken());
        });

        // --- meal-webhook-inbox (Trendyol GO Meal webhook stage pilot) ---
        var mealWebhookInbox = new Command(
            "meal-webhook-inbox",
            "List the Trendyol GO Meal webhook stage pilot's received events (no payloads), or replay one.");
        var optMwbLimit = new Option<int>("--limit", () => 20, "How many of the newest events to list");
        var optMwbReplay = new Option<string?>("--replay", "Event id of a Failed or Processed event to process again");
        mealWebhookInbox.AddOption(optMwbLimit);
        mealWebhookInbox.AddOption(optMwbReplay);
        mealWebhookInbox.SetHandler(async (InvocationContext context) =>
        {
            var p = context.ParseResult;
            context.ExitCode = await CliMealWebhookCommands.InboxAsync(
                host,
                p.GetValueForOption(optMwbLimit),
                p.GetValueForOption(optMwbReplay),
                context.GetCancellationToken());
        });

        return new RootCommand("Wasla CLI — operational tool for tenant onboarding, migrations, and secrets.")
        {
            addTenant,
            provisionSignupRequest,
            addCentralAdmin,
            resetCentralAdminPassword,
            resetPassword,
            listCentralAdmins,
            migrateCentral,
            seedTurkeyReferenceData,
            seedAddressReferenceData,
            migrateTenant,
            migrateAll,
            migrationStatus,
            deleteTenant,
            resetTenantDb,
            resetAllTenantDbs,
            seedTenantAdmin,
            updateTenantProfile,
            listTenants,
            encrypt,
            createUser,
            generatePrintBridgeToken,
            seedPrintJob,
            listPrintJobs,
            mealWebhookIntegrator,
            mealWebhookInbox
        };
    }
}
