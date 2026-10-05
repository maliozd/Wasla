using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Security;
using Wasla.Cli;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;

static bool IsCentralPasswordReset(string[] a)
{
    if (!string.Equals(a[0], "reset-password", StringComparison.OrdinalIgnoreCase))
        return false;

    for (var i = 1; i < a.Length; i++)
    {
        if (!string.Equals(a[i], "--scope", StringComparison.OrdinalIgnoreCase))
            continue;

        return i + 1 < a.Length
            && string.Equals(a[i + 1], "central", StringComparison.OrdinalIgnoreCase);
    }

    return false;
}

static bool HasHelpFlag(string[] a) =>
    a.Any(x => string.Equals(x, "--help", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(x, "-h", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(x, "-?", StringComparison.OrdinalIgnoreCase));

// Custom help behavior:
// - no args => general help
// - --help/-h/-? => general help
// - help [command] => custom help (and does NOT require ENCRYPTION_MASTER_KEY)
// - unknown command => print unknown + general help
if (args.Length == 0 || HasHelpFlag(args))
{
    CliHelpPrinter.PrintGeneralHelp();
    return 0;
}

if (string.Equals(args[0], "help", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1]))
        CliHelpPrinter.PrintCommandHelp(args[1]);
    else
        CliHelpPrinter.PrintGeneralHelp();

    return 0;
}

// If the first token isn't a known command and isn't an option, show custom help.
if (!args[0].StartsWith("-", StringComparison.Ordinal) && !CliHelpPrinter.IsKnownCommand(args[0]))
{
    Console.WriteLine($"Unknown command: {args[0]}");
    Console.WriteLine();
    CliHelpPrinter.PrintGeneralHelp();
    return 2;
}

if (string.Equals(args[0], "hash-password", StringComparison.OrdinalIgnoreCase))
    return CliCommands.HashPassword(args);

if (string.Equals(args[0], "add-central-admin", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(args[0], "reset-central-admin-password", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(args[0], "list-central-admins", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(args[0], "disable-central-admin", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(args[0], "enable-central-admin", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(args[0], "generate-print-bridge-token", StringComparison.OrdinalIgnoreCase) ||
    IsCentralPasswordReset(args))
{
    // These commands operate on CentralDb only and do not require ENCRYPTION_MASTER_KEY.
}
else
{
// All non-help commands require master key.
AesSecretManager.ValidateMasterKeyOrThrow();
}


var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: false,
    reloadOnChange: false);

builder.Services.AddDbContext<CentralDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("CentralDb")));

builder.Services.AddSingleton<ISecretManager, AesSecretManager>();
builder.Services.Configure<CustomerOnboardingOptions>(builder.Configuration.GetSection(CustomerOnboardingOptions.SectionName));
builder.Services.AddScoped<ITenantDatabaseProvisioningOperations, SqlServerTenantDatabaseProvisioningOperations>();
builder.Services.AddScoped<IPendingRegistrationProvisioningService, PendingRegistrationProvisioningService>();
builder.Services.AddWaslaEmail(builder.Configuration);

var host = builder.Build();

// --- provision-signup-request ---
var provisionSignupRequest = new Command(
    "provision-signup-request",
    "Provision a paid PendingRegistration into a live tenant (DB + central registry + owner user).");

var optRegistrationId = new Option<Guid>("--registration-id", "PendingRegistration id (GUID)") { IsRequired = true };
var optPsrDryRun = new Option<bool>("--dry-run", "Validate only; do not create database, tenant, or user");
var optPsrForce = new Option<bool>("--force", "Retry when tenant database exists or registration is Provisioned without a tenant row");
var optPsrSqlServer = new Option<string?>("--sql-server", "SQL Server instance; default from config CustomerDb:ServerInstance or '.'");
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

// --- add-customer ---
var addCustomer = new Command("add-customer", "Onboard a new customer (DB + central registry + first admin).");

var optName = new Option<string>("--name", "Display name") { IsRequired = true };
var optSlug = new Option<string>("--slug", "URL-friendly slug, e.g. ahmet-pizza") { IsRequired = true };
var optDomain = new Option<string>("--domain", "Primary domain, e.g. ahmet.Wasla.com") { IsRequired = true };
var optAdminEmail = new Option<string>("--admin-email", "First admin user email") { IsRequired = true };
var optAdminPassword = new Option<string>("--admin-password", "First admin password (visible in shell history)") { IsRequired = true };
var optAdminName = new Option<string>("--admin-name", () => "Admin", "Admin full name");
var optSqlServer = new Option<string?>("--sql-server", "SQL Server instance; default from config CustomerDb:ServerInstance or '.'");
var optSqlAuth = new Option<string>("--sql-auth", () => "trusted", "Trusted Windows auth, or 'sql:username:password'");
var optBusinessPhone = new Option<string?>("--business-phone", "Restaurant business phone (required by the setup checklist)");
var optCity = new Option<string?>("--city", "Restaurant city (the setup checklist needs a city or country)");
var optCountry = new Option<string?>("--country", "Restaurant country (the setup checklist needs a city or country)");

addCustomer.AddOption(optName);
addCustomer.AddOption(optSlug);
addCustomer.AddOption(optDomain);
addCustomer.AddOption(optAdminEmail);
addCustomer.AddOption(optAdminPassword);
addCustomer.AddOption(optAdminName);
addCustomer.AddOption(optSqlServer);
addCustomer.AddOption(optSqlAuth);
addCustomer.AddOption(optBusinessPhone);
addCustomer.AddOption(optCity);
addCustomer.AddOption(optCountry);

addCustomer.SetHandler(async (InvocationContext context) =>
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
    context.ExitCode = await CliCommands.AddCustomerAsync(
        host, name, slug, domain, adminEmail, adminPassword, adminName, sqlServer, sqlAuth, contact, ct);
});

// --- update-customer-profile ---
var updateCustomerProfile = new Command(
    "update-customer-profile",
    "Set the restaurant business phone, city or country used by the setup checklist (idempotent).");
var optProfileTenant = new Option<string>("--tenant", "Customer slug or id in CentralDb") { IsRequired = true };
var optProfilePhone = new Option<string?>("--business-phone", "Restaurant business phone");
var optProfileCity = new Option<string?>("--city", "Restaurant city");
var optProfileCountry = new Option<string?>("--country", "Restaurant country");
updateCustomerProfile.AddOption(optProfileTenant);
updateCustomerProfile.AddOption(optProfilePhone);
updateCustomerProfile.AddOption(optProfileCity);
updateCustomerProfile.AddOption(optProfileCountry);
updateCustomerProfile.SetHandler(async (InvocationContext context) =>
{
    var p = context.ParseResult;
    context.ExitCode = await CliCommands.UpdateCustomerProfileAsync(
        host,
        p.GetValueForOption(optProfileTenant),
        new TenantContactUpdate(
            p.GetValueForOption(optProfilePhone),
            p.GetValueForOption(optProfileCity),
            p.GetValueForOption(optProfileCountry)),
        context.GetCancellationToken());
});

// --- migrate-central ---
var migrateCentral = new Command("migrate-central", "Apply pending EF Core migrations for CentralDb (customer registry).");

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

// --- migrate-customer ---
var migrateCustomer = new Command("migrate-customer", "Apply pending CustomerDb migrations for a single customer.");

var optMcSlug = new Option<string?>("--slug", "Customer slug in CentralDb");
var optMcCid = new Option<string?>("--customer-id", "Customer id (GUID) in CentralDb");
migrateCustomer.AddOption(optMcSlug);
migrateCustomer.AddOption(optMcCid);

migrateCustomer.SetHandler(async (InvocationContext context) =>
{
    var p = context.ParseResult;
    context.ExitCode = await CliCommands.MigrateCustomerAsync(
        host,
        p.GetValueForOption(optMcSlug),
        p.GetValueForOption(optMcCid),
        context.GetCancellationToken());
});

// --- migration-status ---
var migrationStatus = new Command("migration-status", "Show CentralDb and per-customer CustomerDb migration status.");

migrationStatus.SetHandler(async (InvocationContext context) =>
{
    context.ExitCode = await CliCommands.MigrationStatusAsync(host, context.GetCancellationToken());
});

// --- migrate-all-customers ---
var migrateAll = new Command("migrate-all-customers", "Apply pending CustomerDb migrations to every (or selected) active customer database.");

var optDryRun = new Option<bool>("--dry-run", "List pending migrations only, do not apply");
var optOnly = new Option<string[]>("--only", "Migrate only these customer slugs (repeatable)")
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
    context.ExitCode = await CliCommands.MigrateAllCustomersAsync(host, dry, only, ct);
});

// --- list-customers ---
var listCustomers = new Command("list-customers", "List active customers from CentralDb.");

listCustomers.SetHandler(async (InvocationContext context) =>
{
    context.ExitCode = await CliCommands.ListCustomersAsync(host, context.GetCancellationToken());
});

// --- add-central-admin ---
var addCentralAdmin = new Command("add-central-admin", "Create a CentralDb central admin user (no customer secrets).");
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

// --- disable-central-admin / enable-central-admin ---
Command CentralAdminStatusCommand(string name, string description, bool enable)
{
    var command = new Command(name, description) { TreatUnmatchedTokensAsErrors = true };
    var optEmail = new Option<string>("--email", "Central admin email") { IsRequired = true };
    var optDryRun = new Option<bool>("--dry-run", "Resolve the account and show the change only; do not write");
    command.AddOption(optEmail);
    command.AddOption(optDryRun);
    command.SetHandler(async (InvocationContext context) =>
    {
        var p = context.ParseResult;
        context.ExitCode = await CliCommands.SetCentralAdminEnabledAsync(
            host,
            p.GetValueForOption(optEmail),
            enable,
            p.GetValueForOption(optDryRun),
            context.GetCancellationToken());
    });
    return command;
}

var disableCentralAdmin = CentralAdminStatusCommand(
    "disable-central-admin",
    "Disable a central admin and sign out all of their existing sessions.",
    enable: false);
var enableCentralAdmin = CentralAdminStatusCommand(
    "enable-central-admin",
    "Enable a disabled central admin. Sessions from before the disable stay signed out.",
    enable: true);

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
var createUser = new Command("create-user", "Add a user to an existing customer's database.");

var optCustSlug = new Option<string>("--customer-slug", "Customer slug in CentralDb") { IsRequired = true };
var optEmail = new Option<string>("--email", "User email") { IsRequired = true };
var optPassword = new Option<string>("--password", "Plaintext password (visible in history)") { IsRequired = true };
var optRole = new Option<string>("--role", "Owner, Manager, Kitchen, Cashier, or Viewer") { IsRequired = true };
var optFullName = new Option<string?>("--name", "User full name");

createUser.AddOption(optCustSlug);
createUser.AddOption(optEmail);
createUser.AddOption(optPassword);
createUser.AddOption(optRole);
createUser.AddOption(optFullName);

createUser.SetHandler(async (InvocationContext context) =>
{
    var p = context.ParseResult;
    context.ExitCode = await CliCommands.CreateUserAsync(
        host,
        p.GetValueForOption(optCustSlug)!,
        p.GetValueForOption(optEmail)!,
        p.GetValueForOption(optPassword)!,
        p.GetValueForOption(optRole)!,
        p.GetValueForOption(optFullName),
        context.GetCancellationToken());
});

// --- delete-customer ---
var deleteCustomer = new Command("delete-customer", "Permanently delete a customer record and drop its CustomerDb (DESTRUCTIVE).");
var optDelSlug = new Option<string?>("--slug", "Customer slug in CentralDb");
var optDelCid = new Option<string?>("--customer-id", "Customer id (GUID) in CentralDb");
var optConfirm = new Option<bool>("--confirm", "Required. Confirms you understand this is destructive.");
var optForceProd = new Option<bool>("--force-production", "Allow running in Production (still requires --confirm).");
deleteCustomer.AddOption(optDelSlug);
deleteCustomer.AddOption(optDelCid);
deleteCustomer.AddOption(optConfirm);
deleteCustomer.AddOption(optForceProd);
deleteCustomer.SetHandler(async (InvocationContext context) =>
{
    var p = context.ParseResult;
    context.ExitCode = await CliCommands.DeleteCustomerAsync(
        host,
        p.GetValueForOption(optDelSlug),
        p.GetValueForOption(optDelCid),
        p.GetValueForOption(optConfirm),
        p.GetValueForOption(optForceProd),
        context.GetCancellationToken());
});

// --- reset-customer-db ---
var resetCustomerDb = new Command("reset-customer-db", "Drop + recreate + migrate a single CustomerDb, keeping CentralDb customer record (DESTRUCTIVE).");
var optResetSlug = new Option<string?>("--slug", "Customer slug in CentralDb");
var optResetCid = new Option<string?>("--customer-id", "Customer id (GUID) in CentralDb");
resetCustomerDb.AddOption(optResetSlug);
resetCustomerDb.AddOption(optResetCid);
resetCustomerDb.AddOption(optConfirm);
resetCustomerDb.AddOption(optForceProd);
resetCustomerDb.SetHandler(async (InvocationContext context) =>
{
    var p = context.ParseResult;
    context.ExitCode = await CliCommands.ResetCustomerDbAsync(
        host,
        p.GetValueForOption(optResetSlug),
        p.GetValueForOption(optResetCid),
        p.GetValueForOption(optConfirm),
        p.GetValueForOption(optForceProd),
        context.GetCancellationToken());
});

// --- reset-all-customer-dbs ---
var resetAllCustomerDbs = new Command("reset-all-customer-dbs", "Drop + recreate + migrate ALL active CustomerDbs (DESTRUCTIVE).");
resetAllCustomerDbs.AddOption(optConfirm);
resetAllCustomerDbs.AddOption(optForceProd);
resetAllCustomerDbs.SetHandler(async (InvocationContext context) =>
{
    var p = context.ParseResult;
    context.ExitCode = await CliCommands.ResetAllCustomerDbsAsync(
        host,
        p.GetValueForOption(optConfirm),
        p.GetValueForOption(optForceProd),
        context.GetCancellationToken());
});

// --- seed-customer-admin ---
var seedCustomerAdmin = new Command("seed-customer-admin", "Create an Owner admin user in an existing customer DB if missing.");
var optSeedSlug = new Option<string>("--slug", "Customer slug in CentralDb") { IsRequired = true };
var optSeedEmail = new Option<string>("--admin-email", "Admin email") { IsRequired = true };
var optSeedPassword = new Option<string>("--admin-password", "Admin password (visible in history)") { IsRequired = true };
var optSeedName = new Option<string?>("--admin-name", "Admin full name");
seedCustomerAdmin.AddOption(optSeedSlug);
seedCustomerAdmin.AddOption(optSeedEmail);
seedCustomerAdmin.AddOption(optSeedPassword);
seedCustomerAdmin.AddOption(optSeedName);
seedCustomerAdmin.SetHandler(async (InvocationContext context) =>
{
    var p = context.ParseResult;
    context.ExitCode = await CliCommands.SeedCustomerAdminAsync(
        host,
        p.GetValueForOption(optSeedSlug)!,
        p.GetValueForOption(optSeedEmail)!,
        p.GetValueForOption(optSeedPassword)!,
        p.GetValueForOption(optSeedName),
        context.GetCancellationToken());
});

// --- generate-print-bridge-token ---
var generatePrintBridgeToken = new Command("generate-print-bridge-token", "Register a Print Bridge device and print a one-time agent token.");
var optPbCustomerId = new Option<Guid?>("--customer-id", "Customer id in CentralDb");
var optPbSlug = new Option<string?>("--slug", "Customer slug in CentralDb");
var optPbDeviceName = new Option<string?>("--name", "Print Bridge device display name");
generatePrintBridgeToken.AddOption(optPbCustomerId);
generatePrintBridgeToken.AddOption(optPbSlug);
generatePrintBridgeToken.AddOption(optPbDeviceName);
generatePrintBridgeToken.SetHandler(async (InvocationContext context) =>
{
    var p = context.ParseResult;
    context.ExitCode = await CliCommands.GeneratePrintBridgeTokenAsync(
        host,
        p.GetValueForOption(optPbCustomerId),
        p.GetValueForOption(optPbSlug),
        p.GetValueForOption(optPbDeviceName),
        context.GetCancellationToken());
});

// --- seed-print-job (dev helper) ---
var seedPrintJob = new Command("seed-print-job", "Create a pending receipt PrintJob for Print Bridge testing.");
var optSpjSlug = new Option<string?>("--slug", "Customer slug in CentralDb");
var optSpjCustomerId = new Option<string?>("--customer-id", "Customer id in CentralDb");
seedPrintJob.AddOption(optSpjSlug);
seedPrintJob.AddOption(optSpjCustomerId);
seedPrintJob.SetHandler(async (InvocationContext context) =>
{
    var p = context.ParseResult;
    context.ExitCode = await CliCommands.SeedPrintJobAsync(
        host,
        p.GetValueForOption(optSpjSlug),
        p.GetValueForOption(optSpjCustomerId),
        context.GetCancellationToken());
});

// --- list-print-jobs ---
var listPrintJobs = new Command("list-print-jobs", "List recent PrintJobs for a customer (dev/testing).");
listPrintJobs.AddOption(optSpjSlug);
listPrintJobs.AddOption(optSpjCustomerId);
listPrintJobs.SetHandler(async (InvocationContext context) =>
{
    var p = context.ParseResult;
    context.ExitCode = await CliCommands.ListPrintJobsAsync(
        host,
        p.GetValueForOption(optSpjSlug),
        p.GetValueForOption(optSpjCustomerId),
        context.GetCancellationToken());
});

var root = new RootCommand("orderhub — operational CLI for customer onboarding, migrations, and secrets.")
{
    addCustomer,
    provisionSignupRequest,
    addCentralAdmin,
    resetCentralAdminPassword,
    resetPassword,
    listCentralAdmins,
    disableCentralAdmin,
    enableCentralAdmin,
    migrateCentral,
    seedTurkeyReferenceData,
    seedAddressReferenceData,
    migrateCustomer,
    migrateAll,
    migrationStatus,
    deleteCustomer,
    resetCustomerDb,
    resetAllCustomerDbs,
    seedCustomerAdmin,
    updateCustomerProfile,
    listCustomers,
    encrypt,
    createUser,
    generatePrintBridgeToken,
    seedPrintJob,
    listPrintJobs
};

return await root.InvokeAsync(args);
