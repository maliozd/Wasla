using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Cli;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Security;

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

// All non-help commands require master key.
AesSecretManager.ValidateMasterKeyOrThrow();


var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: false,
    reloadOnChange: false);

builder.Services.AddDbContext<CentralDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("CentralDb")));

builder.Services.AddSingleton<ISecretManager, AesSecretManager>();

var host = builder.Build();

// --- add-customer ---
var addCustomer = new Command("add-customer", "Onboard a new customer (DB + central registry + first admin).");

var optName = new Option<string>("--name", "Display name") { IsRequired = true };
var optSlug = new Option<string>("--slug", "URL-friendly slug, e.g. ahmet-pizza") { IsRequired = true };
var optDomain = new Option<string>("--domain", "Primary domain, e.g. ahmet.orderhub.com") { IsRequired = true };
var optAdminEmail = new Option<string>("--admin-email", "First admin user email") { IsRequired = true };
var optAdminPassword = new Option<string>("--admin-password", "First admin password (visible in shell history)") { IsRequired = true };
var optAdminName = new Option<string>("--admin-name", () => "Admin", "Admin full name");
var optSqlServer = new Option<string?>("--sql-server", "SQL Server instance; default from config CustomerDb:ServerInstance or '.'");
var optSqlAuth = new Option<string>("--sql-auth", () => "trusted", "Trusted Windows auth, or 'sql:username:password'");

addCustomer.AddOption(optName);
addCustomer.AddOption(optSlug);
addCustomer.AddOption(optDomain);
addCustomer.AddOption(optAdminEmail);
addCustomer.AddOption(optAdminPassword);
addCustomer.AddOption(optAdminName);
addCustomer.AddOption(optSqlServer);
addCustomer.AddOption(optSqlAuth);

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
    context.ExitCode = await CliCommands.AddCustomerAsync(
        host, name, slug, domain, adminEmail, adminPassword, adminName, sqlServer, sqlAuth, ct);
});

// --- migrate-central ---
var migrateCentral = new Command("migrate-central", "Apply pending EF Core migrations for CentralDb (customer registry).");

migrateCentral.SetHandler(async (InvocationContext context) =>
{
    context.ExitCode = await CliCommands.MigrateCentralAsync(host, context.GetCancellationToken());
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
var optRole = new Option<string>("--role", "Owner, Manager, or Staff") { IsRequired = true };
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

var root = new RootCommand("orderhub — operational CLI for customer onboarding, migrations, and secrets.")
{
    addCustomer,
    migrateCentral,
    migrateCustomer,
    migrateAll,
    migrationStatus,
    deleteCustomer,
    resetCustomerDb,
    resetAllCustomerDbs,
    seedCustomerAdmin,
    listCustomers,
    encrypt,
    createUser
};

return await root.InvokeAsync(args);
