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

var root = new RootCommand("orderhub — operational CLI for customer onboarding, migrations, and secrets.")
{
    addCustomer, migrateAll, listCustomers, encrypt, createUser
};

return await root.InvokeAsync(args);
