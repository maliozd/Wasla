namespace OrderHub.Cli;

internal static class CliHelpPrinter
{
    private static readonly string[] CustomerCommands =
    [
        "add-customer",
        "list-customers",
        "delete-customer",
        "reset-customer-db",
        "reset-all-customer-dbs",
        "seed-customer-admin"
    ];

    private static readonly string[] MigrationCommands =
    [
        "migrate-central",
        "seed-turkey-reference-data",
        "seed-address-reference-data",
        "migrate-customer",
        "migrate-all-customers",
        "migration-status"
    ];

    private static readonly string[] UtilityCommands =
    [
        "encrypt",
        "hash-password",
        "generate-print-bridge-token",
        "seed-print-job",
        "list-print-jobs",
        "help"
    ];

    private static readonly string[] CentralAdminCommands =
    [
        "add-central-admin",
        "reset-central-admin-password",
        "list-central-admins"
    ];

    private static readonly HashSet<string> AllCommands = new(
        CustomerCommands
            .Concat(MigrationCommands)
            .Concat(CentralAdminCommands)
            .Concat(UtilityCommands),
        StringComparer.OrdinalIgnoreCase);

    public static void PrintGeneralHelp()
    {
        Console.WriteLine("OrderHub CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- <command> [options]");
        Console.WriteLine();

        Console.WriteLine("Customer commands:");
        Console.WriteLine("  add-customer              Create a customer, customer database, migrations and first admin user.");
        Console.WriteLine("  list-customers            List customers from CentralDb.");
        Console.WriteLine("  delete-customer           Delete customer record and drop customer database. Requires --confirm.");
        Console.WriteLine("  reset-customer-db         Drop/recreate a single customer database. Requires --confirm.");
        Console.WriteLine("  reset-all-customer-dbs    Drop/recreate all active customer databases. Requires --confirm.");
        Console.WriteLine("  seed-customer-admin       Create an Owner admin user for a customer DB if missing.");
        Console.WriteLine();

        Console.WriteLine("Migration commands:");
        Console.WriteLine("  migrate-central           Apply pending CentralDb migrations.");
        Console.WriteLine("  seed-turkey-reference-data Seed all 81 Turkish cities and districts into CentralDb.");
        Console.WriteLine("  seed-address-reference-data Import address reference data (country, city, district; neighborhoods/streets when files are available).");
        Console.WriteLine("  migrate-customer          Apply pending CustomerDb migrations for one customer.");
        Console.WriteLine("  migrate-all-customers     Apply pending CustomerDb migrations for all active customers.");
        Console.WriteLine("  migration-status          Show CentralDb and CustomerDb migration status.");
        Console.WriteLine();

        Console.WriteLine("Central admin commands:");
        Console.WriteLine("  add-central-admin          Create a central admin user in CentralDb.");
        Console.WriteLine("  reset-central-admin-password Reset a central admin password in CentralDb.");
        Console.WriteLine("  list-central-admins        List central admin users (safe fields only).");
        Console.WriteLine();

        Console.WriteLine("Utility commands:");
        Console.WriteLine("  encrypt                   Encrypt a plaintext value using ENCRYPTION_MASTER_KEY.");
        Console.WriteLine("  hash-password             Print a BCrypt hash (no configuration or master key required).");
        Console.WriteLine("  generate-print-bridge-token Register a Print Bridge device and print a one-time agent token.");
        Console.WriteLine("  seed-print-job            Create a pending receipt PrintJob for Print Bridge testing.");
        Console.WriteLine("  list-print-jobs           List recent PrintJobs for a customer (dev/testing).");
        Console.WriteLine("  help                      Show CLI help.");
        Console.WriteLine();

        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- add-customer --name \"Ahmet Pizza\" --slug ahmet --domain ahmet.orderhub.local --admin-email admin@ahmet.com --admin-password \"Test123!\" --admin-name \"Ahmet Admin\"");
        Console.WriteLine();
        Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- list-customers");
        Console.WriteLine();
        Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- migrate-all-customers");
        Console.WriteLine();
        Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- delete-customer --slug ahmet --confirm");
        Console.WriteLine();

        Console.WriteLine("Warning:");
        Console.WriteLine("  delete-customer, reset-customer-db and reset-all-customer-dbs are destructive commands.");
        Console.WriteLine("  They require --confirm and are blocked in Production unless --force-production is explicitly provided.");
    }

    public static void PrintCommandHelp(string command)
    {
        if (!AllCommands.Contains(command))
        {
            Console.WriteLine($"Unknown command: {command}");
            Console.WriteLine();
            PrintGeneralHelp();
            return;
        }

        switch (command.ToLowerInvariant())
        {
            case "add-customer":
                Console.WriteLine("add-customer");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  add-customer --name <name> --slug <slug> --domain <domain> --admin-email <email> --admin-password <password> --admin-name <name>");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- add-customer --name \"Ahmet Pizza\" --slug ahmet --domain ahmet.orderhub.local --admin-email admin@ahmet.com --admin-password \"Test123!\" --admin-name \"Ahmet Admin\"");
                return;

            case "delete-customer":
                Console.WriteLine("delete-customer (DESTRUCTIVE)");
                Console.WriteLine();
                Console.WriteLine("What it does:");
                Console.WriteLine("  - Drops the customer database (CustomerDb)");
                Console.WriteLine("  - Deletes the Customer record from CentralDb");
                Console.WriteLine();
                Console.WriteLine("Safety:");
                Console.WriteLine("  - Requires --confirm");
                Console.WriteLine("  - Blocked in Production unless --force-production is provided");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  delete-customer --slug <slug> --confirm");
                Console.WriteLine("  delete-customer --customer-id <guid> --confirm");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- delete-customer --slug ahmet --confirm");
                return;

            case "reset-customer-db":
                Console.WriteLine("reset-customer-db (DESTRUCTIVE)");
                Console.WriteLine();
                Console.WriteLine("What it does:");
                Console.WriteLine("  - Keeps the CentralDb Customer record");
                Console.WriteLine("  - Drops + recreates the CustomerDb (all users/orders/platform connections/etc are deleted)");
                Console.WriteLine("  - Applies all CustomerDb migrations");
                Console.WriteLine();
                Console.WriteLine("Safety:");
                Console.WriteLine("  - Requires --confirm");
                Console.WriteLine("  - Blocked in Production unless --force-production is provided");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  reset-customer-db --slug <slug> --confirm");
                Console.WriteLine("  reset-customer-db --customer-id <guid> --confirm");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- reset-customer-db --slug ahmet --confirm");
                Console.WriteLine();
                Console.WriteLine("Tip:");
                Console.WriteLine("  After reset, recreate admin user with seed-customer-admin.");
                return;

            case "reset-all-customer-dbs":
                Console.WriteLine("reset-all-customer-dbs (DESTRUCTIVE)");
                Console.WriteLine();
                Console.WriteLine("What it does:");
                Console.WriteLine("  - Keeps CentralDb Customer records");
                Console.WriteLine("  - Drops + recreates + migrates ALL active CustomerDbs (all tenant data is deleted)");
                Console.WriteLine();
                Console.WriteLine("Safety:");
                Console.WriteLine("  - Requires --confirm");
                Console.WriteLine("  - Blocked in Production unless --force-production is provided");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  reset-all-customer-dbs --confirm");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- reset-all-customer-dbs --confirm");
                return;

            case "migrate-all-customers":
                Console.WriteLine("migrate-all-customers");
                Console.WriteLine();
                Console.WriteLine("What it does:");
                Console.WriteLine("  Applies pending CustomerDb migrations to all active customers.");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- migrate-all-customers");
                return;

            case "encrypt":
                Console.WriteLine("encrypt");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  encrypt <plaintext>");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- encrypt \"fake-trendyol-key\"");
                return;

            case "hash-password":
                Console.WriteLine("hash-password");
                Console.WriteLine();
                Console.WriteLine("Prints a BCrypt hash of the given password.");
                Console.WriteLine("Does not use ENCRYPTION_MASTER_KEY.");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  hash-password --password <plaintext>");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- hash-password --password \"Test123!\"");
                return;

            case "add-central-admin":
                Console.WriteLine("add-central-admin");
                Console.WriteLine();
                Console.WriteLine("Creates a central admin user in CentralDb.");
                Console.WriteLine("Usage:");
                Console.WriteLine("  add-central-admin --email <email> --password <password> --display-name <name>");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- add-central-admin --email admin@orderhub.com --password \"Test123!\" --display-name \"Central Admin\"");
                return;

            case "reset-central-admin-password":
                Console.WriteLine("reset-central-admin-password");
                Console.WriteLine();
                Console.WriteLine("Resets the password for a central admin user (CentralDb).");
                Console.WriteLine("Usage:");
                Console.WriteLine("  reset-central-admin-password --email <email> --password <newPassword>");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\OrderHub.Cli -- reset-central-admin-password --email admin@orderhub.com --password \"NewPassword123!\"");
                return;

            case "list-central-admins":
                Console.WriteLine("list-central-admins");
                Console.WriteLine();
                Console.WriteLine("Lists central admin users (safe output only).");
                Console.WriteLine("Usage:");
                Console.WriteLine("  list-central-admins");
                return;

            default:
                Console.WriteLine($"{command}");
                Console.WriteLine();
                Console.WriteLine("Use:");
                Console.WriteLine($"  dotnet run --project src\\OrderHub.Cli -- --help");
                Console.WriteLine("Or:");
                Console.WriteLine($"  dotnet run --project src\\OrderHub.Cli -- help {command}");
                return;
        }
    }

    public static bool IsKnownCommand(string command) => AllCommands.Contains(command);
}

