namespace Wasla.Cli;

internal static class CliHelpPrinter
{
    private static readonly string[] TenantCommands =
    [
        "add-tenant",
        "provision-signup-request",
        "list-tenants",
        "delete-tenant",
        "reset-tenant-db",
        "reset-all-tenant-dbs",
        "seed-tenant-admin",
        "update-tenant-profile"
    ];

    private static readonly string[] MigrationCommands =
    [
        "migrate-central",
        "seed-turkey-reference-data",
        "seed-address-reference-data",
        "migrate-tenant",
        "migrate-all-tenants",
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

    private static readonly string[] AccountCommands =
    [
        "reset-password"
    ];

    private static readonly HashSet<string> AllCommands = new(
        TenantCommands
            .Concat(MigrationCommands)
            .Concat(CentralAdminCommands)
            .Concat(AccountCommands)
            .Concat(UtilityCommands),
        StringComparer.OrdinalIgnoreCase);

    public static void PrintGeneralHelp()
    {
        Console.WriteLine("Wasla CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- <command> [options]");
        Console.WriteLine();

        Console.WriteLine("Tenant commands:");
        Console.WriteLine("  add-tenant                Create a tenant, tenant database, migrations and first admin user.");
        Console.WriteLine("  provision-signup-request  Provision a paid PendingRegistration into a live tenant.");
        Console.WriteLine("  list-tenants              List tenants from CentralDb.");
        Console.WriteLine("  delete-tenant             Delete tenant record and drop tenant database. Requires --confirm.");
        Console.WriteLine("  reset-tenant-db           Drop/recreate a single tenant database. Requires --confirm.");
        Console.WriteLine("  reset-all-tenant-dbs      Drop/recreate all active tenant databases. Requires --confirm.");
        Console.WriteLine("  seed-tenant-admin         Create an Owner admin user for a tenant DB if missing.");
        Console.WriteLine("  update-tenant-profile     Set the restaurant phone, city or country used by the setup checklist.");
        Console.WriteLine();

        Console.WriteLine("Migration commands:");
        Console.WriteLine("  migrate-central           Apply pending CentralDb migrations.");
        Console.WriteLine("  seed-turkey-reference-data Seed all 81 Turkish cities and districts into CentralDb.");
        Console.WriteLine("  seed-address-reference-data Import address reference data (country, city, district; neighborhoods/streets when files are available).");
        Console.WriteLine("  migrate-tenant            Apply pending TenantDb migrations for one tenant.");
        Console.WriteLine("  migrate-all-tenants       Apply pending TenantDb migrations for all active tenants.");
        Console.WriteLine("  migration-status          Show CentralDb and TenantDb migration status; --require-current gates a release.");
        Console.WriteLine();

        Console.WriteLine("Central admin commands:");
        Console.WriteLine("  add-central-admin          Create a central admin user in CentralDb.");
        Console.WriteLine("  reset-central-admin-password Reset a central admin password in CentralDb.");
        Console.WriteLine("  list-central-admins        List central admin users (safe fields only).");
        Console.WriteLine();

        Console.WriteLine("Account commands:");
        Console.WriteLine("  reset-password            Reset an existing central or tenant user password.");
        Console.WriteLine();

        Console.WriteLine("Utility commands:");
        Console.WriteLine("  encrypt                   Encrypt a plaintext value using ENCRYPTION_MASTER_KEY.");
        Console.WriteLine("  hash-password             Print a BCrypt hash (no configuration or master key required).");
        Console.WriteLine("  generate-print-bridge-token Register a Print Bridge device and print a one-time agent token.");
        Console.WriteLine("  seed-print-job            Create a pending receipt PrintJob for Print Bridge testing.");
        Console.WriteLine("  list-print-jobs           List recent PrintJobs for a tenant (dev/testing).");
        Console.WriteLine("  help                      Show CLI help.");
        Console.WriteLine();

        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- add-tenant --name \"Ahmet Pizza\" --slug ahmet --domain ahmet.Wasla.local --admin-email admin@ahmet.com --admin-password \"Test123!\" --admin-name \"Ahmet Admin\"");
        Console.WriteLine();
        Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- list-tenants");
        Console.WriteLine();
        Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- migrate-all-tenants");
        Console.WriteLine();
        Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- delete-tenant --slug ahmet --confirm");
        Console.WriteLine();

        Console.WriteLine("Warning:");
        Console.WriteLine("  delete-tenant, reset-tenant-db and reset-all-tenant-dbs are destructive commands.");
        Console.WriteLine("  They require --confirm and are blocked in Production unless --force-production is explicitly provided.");
        Console.WriteLine("  With no DOTNET_ENVIRONMENT/ASPNETCORE_ENVIRONMENT set, the CLI counts as Production.");
        Console.WriteLine("  They refuse CentralDb, SQL Server system databases, a database named by more than one tenant,");
        Console.WriteLine("  and a tenant whose connection string targets a database other than its registered one.");
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
            case "add-tenant":
                Console.WriteLine("add-tenant");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  add-tenant --name <name> --slug <slug> --domain <domain> --admin-email <email> --admin-password <password> --admin-name <name>");
                Console.WriteLine("               [--business-phone <phone>] [--city <city>] [--country <country>]");
                Console.WriteLine();
                Console.WriteLine("Also records the tenant membership. The dashboard setup checklist needs a business phone");
                Console.WriteLine("and a city or country; without them, set them later with update-tenant-profile.");
                Console.WriteLine();
                Console.WriteLine("The tenant database is created on --sql-server, or on TenantDb:ServerInstance.");
                Console.WriteLine("There is no default server: without one, nothing is created.");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- add-tenant --name \"Ahmet Pizza\" --slug ahmet --domain ahmet.Wasla.local --admin-email admin@ahmet.com --admin-password \"Test123!\" --admin-name \"Ahmet Admin\" --business-phone \"+90 212 000 00 00\" --city Istanbul");
                return;

            case "update-tenant-profile":
                Console.WriteLine("update-tenant-profile");
                Console.WriteLine();
                Console.WriteLine("Sets the restaurant contact used by the dashboard setup checklist. Creates the tenant's");
                Console.WriteLine("membership row when missing. Omitted fields keep their value; repeating a call changes nothing.");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  update-tenant-profile --tenant <slug-or-id> [--business-phone <phone>] [--city <city>] [--country <country>]");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- update-tenant-profile --tenant ahmet --business-phone \"+90 212 000 00 00\" --city Istanbul");
                return;

            case "provision-signup-request":
                Console.WriteLine("provision-signup-request");
                Console.WriteLine();
                Console.WriteLine("Provisions a PaymentSucceeded PendingRegistration into a live tenant.");
                Console.WriteLine("Uses the stored PasswordHash for the first owner user (no plaintext password).");
                Console.WriteLine("The tenant database is created on --sql-server, or on TenantDb:ServerInstance.");
                Console.WriteLine("There is no default server: without one, nothing is created.");
                Console.WriteLine("--force only retries a registration marked Provisioned without a tenant row; it never adopts a");
                Console.WriteLine("database that lacks this registration's ownership marker.");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  provision-signup-request --registration-id <guid> [--dry-run] [--force] [--sql-server <instance>] [--sql-auth trusted|sql:user:pass]");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- provision-signup-request --registration-id 00000000-0000-0000-0000-000000000001");
                return;

            case "delete-tenant":
                Console.WriteLine("delete-tenant (DESTRUCTIVE)");
                Console.WriteLine();
                Console.WriteLine("What it does:");
                Console.WriteLine("  - Drops the tenant database (TenantDb)");
                Console.WriteLine("  - Deletes the Tenant record from CentralDb");
                Console.WriteLine();
                Console.WriteLine("Safety:");
                Console.WriteLine("  - Requires --confirm");
                Console.WriteLine("  - Blocked in Production unless --force-production is provided");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  delete-tenant --slug <slug> --confirm");
                Console.WriteLine("  delete-tenant --tenant-id <guid> --confirm");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- delete-tenant --slug ahmet --confirm");
                return;

            case "reset-tenant-db":
                Console.WriteLine("reset-tenant-db (DESTRUCTIVE)");
                Console.WriteLine();
                Console.WriteLine("What it does:");
                Console.WriteLine("  - Keeps the CentralDb Tenant record");
                Console.WriteLine("  - Drops + recreates the TenantDb (all users/orders/platform connections/etc are deleted)");
                Console.WriteLine("  - Applies all TenantDb migrations");
                Console.WriteLine();
                Console.WriteLine("Safety:");
                Console.WriteLine("  - Requires --confirm");
                Console.WriteLine("  - Blocked in Production unless --force-production is provided");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  reset-tenant-db --slug <slug> --confirm");
                Console.WriteLine("  reset-tenant-db --tenant-id <guid> --confirm");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- reset-tenant-db --slug ahmet --confirm");
                Console.WriteLine();
                Console.WriteLine("Tip:");
                Console.WriteLine("  After reset, recreate admin user with seed-tenant-admin.");
                return;

            case "reset-all-tenant-dbs":
                Console.WriteLine("reset-all-tenant-dbs (DESTRUCTIVE)");
                Console.WriteLine();
                Console.WriteLine("What it does:");
                Console.WriteLine("  - Keeps CentralDb Tenant records");
                Console.WriteLine("  - Drops + recreates + migrates ALL active TenantDbs (all tenant data is deleted)");
                Console.WriteLine();
                Console.WriteLine("Safety:");
                Console.WriteLine("  - Requires --confirm");
                Console.WriteLine("  - Blocked in Production unless --force-production is provided");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  reset-all-tenant-dbs --confirm");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- reset-all-tenant-dbs --confirm");
                return;

            case "migrate-all-tenants":
                Console.WriteLine("migrate-all-tenants");
                Console.WriteLine();
                Console.WriteLine("What it does:");
                Console.WriteLine("  Applies pending TenantDb migrations to all active tenants.");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- migrate-all-tenants");
                return;

            case "migration-status":
                Console.WriteLine("migration-status");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  migration-status [--require-current]");
                Console.WriteLine("  --require-current fails when CentralDb or any active TenantDb is pending, unknown, or unreadable.");
                return;

            case "encrypt":
                Console.WriteLine("encrypt");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  encrypt <plaintext>");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- encrypt \"fake-trendyol-key\"");
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
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- hash-password --password \"Test123!\"");
                return;

            case "add-central-admin":
                Console.WriteLine("add-central-admin");
                Console.WriteLine();
                Console.WriteLine("Creates a central admin user in CentralDb.");
                Console.WriteLine("Usage:");
                Console.WriteLine("  add-central-admin --email <email> --password <password> --display-name <name>");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- add-central-admin --email admin@Wasla.com --password \"Test123!\" --display-name \"Central Admin\"");
                return;

            case "reset-central-admin-password":
                Console.WriteLine("reset-central-admin-password");
                Console.WriteLine();
                Console.WriteLine("Resets the password for a central admin user (CentralDb).");
                Console.WriteLine("Usage:");
                Console.WriteLine("  reset-central-admin-password --email <email> --password <newPassword>");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli -- reset-central-admin-password --email admin@Wasla.com --password \"NewPassword123!\"");
                return;

            case "reset-password":
                Console.WriteLine("reset-password");
                Console.WriteLine();
                Console.WriteLine("Resets the password of an existing account. Does not create users.");
                Console.WriteLine("The new password is prompted interactively and is not accepted as --password.");
                Console.WriteLine("--dry-run resolves the account and writes nothing.");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  reset-password --scope central --email <email> [--dry-run]");
                Console.WriteLine("  reset-password --scope tenant --tenant <slug-or-id> --email <email> [--dry-run]");
                Console.WriteLine();
                Console.WriteLine("Examples:");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli\\Wasla.Cli.csproj -- reset-password --scope central --email user@example.com");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli\\Wasla.Cli.csproj -- reset-password --scope tenant --tenant mengen --email user@example.com");
                Console.WriteLine("  dotnet run --project src\\Wasla.Cli\\Wasla.Cli.csproj -- reset-password --scope central --email user@example.com --dry-run");
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
                Console.WriteLine($"  dotnet run --project src\\Wasla.Cli -- --help");
                Console.WriteLine("Or:");
                Console.WriteLine($"  dotnet run --project src\\Wasla.Cli -- help {command}");
                return;
        }
    }

    public static bool IsKnownCommand(string command) => AllCommands.Contains(command);

    /// <summary>Every command name the early gate in Program.cs accepts.</summary>
    internal static IReadOnlyCollection<string> KnownCommands => AllCommands;
}

