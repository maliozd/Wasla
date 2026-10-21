using System.CommandLine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

// Environment variables and the command line override the JSON files, never the other way round (WAS-93).
CliConfiguration.Configure(
    builder.Configuration,
    AppContext.BaseDirectory,
    builder.Environment.EnvironmentName,
    args,
    typeof(CliConfiguration).Assembly);

// Every remaining command reads CentralDb; without it nothing can be checked, so stop before doing anything (WAS-93).
if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("CentralDb")))
{
    Console.Error.WriteLine("ConnectionStrings:CentralDb is not configured.");
    return 2;
}

builder.Services.AddWaslaCliServices(builder.Configuration);

var host = builder.Build();

return await CliCommandTree.Build(host).InvokeAsync(args);
