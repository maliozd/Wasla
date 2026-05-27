using OrderHub.Application.Abstractions.Orders.Services;

namespace OrderHub.Worker.Console;

/// <summary>
/// Lightweight Development-only console presentation layer.
/// No business logic, no database access, no provider calls — formatting only.
/// </summary>
internal static class WorkerConsole
{
    private static readonly object _lock = new();

    public static void WriteStartupBanner(string providerMode, string environmentName, int intervalSeconds)
    {
        const string Bar = "────────────────────────────────────────────────";
        lock (_lock)
        {
            System.Console.WriteLine();
            System.Console.WriteLine($"┌─ OrderHub Worker {Bar}");
            System.Console.WriteLine($"│  Mode        : {providerMode}");
            System.Console.WriteLine($"│  Environment : {environmentName}");
            System.Console.WriteLine($"│  Interval    : {intervalSeconds}s");
            System.Console.WriteLine($"└{Bar}──");
            System.Console.WriteLine();
        }
    }

    public static void WriteCycleStarted(DateTime utcNow, int customerCount, int intervalSeconds)
    {
        lock (_lock)
        {
            WriteColored("▶", ConsoleColor.Cyan);
            System.Console.WriteLine(
                $" Cycle started  {utcNow:HH:mm:ss}  Customers={customerCount}  Interval={intervalSeconds}s");
        }
    }

    public static void WriteCustomerResult(string customerName, IReadOnlyList<OrderSyncConnectionSummary> connections)
    {
        if (connections.Count == 0)
            return;

        lock (_lock)
        {
            WriteColored("  ● ", ConsoleColor.White);
            System.Console.WriteLine(customerName);

            foreach (var c in connections)
            {
                var elapsed = FormatElapsed(c.ElapsedMs);
                var failMark = c.IsFailed ? "  !! FAILED" : string.Empty;
                System.Console.WriteLine(
                    $"    {c.Platform,-20} Store={c.StoreId,-10}  " +
                    $"Fetched={c.FetchedCount}  Inserted={c.InsertedCount}  " +
                    $"Updated={c.UpdatedCount}  Skipped={c.SkippedCount}  {elapsed}{failMark}");
            }
        }
    }

    public static void WriteSyncDisabled(string customerName, string slug)
    {
        lock (_lock)
        {
            WriteColored("  ○ ", ConsoleColor.DarkGray);
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine($"{customerName} ({slug}) — sync disabled");
            System.Console.ResetColor();
        }
    }

    public static void WriteCycleCompleted(
        int customerCount, int connectionCount,
        int fetched, int inserted, int updated, int failed, long elapsedMs)
    {
        lock (_lock)
        {
            WriteColored("✓", failed > 0 ? ConsoleColor.Yellow : ConsoleColor.Green);
            System.Console.WriteLine(
                $" Cycle completed  Customers={customerCount}  Connections={connectionCount}  " +
                $"Fetched={fetched}  Inserted={inserted}  Updated={updated}  Failed={failed}  " +
                FormatElapsed(elapsedMs));
            System.Console.WriteLine();
        }
    }

    public static void WriteWarning(string message)
    {
        lock (_lock)
        {
            WriteColored("⚠ ", ConsoleColor.Yellow);
            System.Console.WriteLine(message);
        }
    }

    public static void WriteError(string message)
    {
        lock (_lock)
        {
            WriteColored("✗ ", ConsoleColor.Red);
            System.Console.WriteLine(message);
        }
    }

    private static string FormatElapsed(long ms)
        => ms >= 1000 ? $"{ms / 1000.0:F1}s" : $"{ms}ms";

    private static void WriteColored(string text, ConsoleColor color)
    {
        System.Console.ForegroundColor = color;
        System.Console.Write(text);
        System.Console.ResetColor();
    }
}
