using Wasla.Application.Abstractions.Orders.Services;

namespace Wasla.Worker.Console;

/// <summary>
/// Lightweight Development-only console presentation layer.
/// No business logic, no database access, no provider calls — formatting only.
/// </summary>
internal static class WorkerConsole
{
    // Table column widths.
    private const int ColPlatform  = 16;
    private const int ColStore     = 12;
    private const int ColFetched   = 8;
    private const int ColInserted  = 9;  // "Inserted" header is 8 chars
    private const int ColUpdated   = 8;
    private const int ColSkipped   = 8;

    // Customer section fixed widths.
    private const int CustomerNameWidth = 28;
    private const int CustomerSepLen    = 60;

    private static readonly object _lock = new();

    // ── Startup banner ────────────────────────────────────────────────────────

    public static void WriteStartupBanner(string providerMode, string environmentName, int intervalSeconds)
    {
        const int W = 48;
        const string Title = "OrderHub Worker";
        int rightDashes = W - Title.Length - 3;

        var modeColor = providerMode.Equals("Real", StringComparison.OrdinalIgnoreCase)
            ? ConsoleColor.Yellow
            : ConsoleColor.Green;

        lock (_lock)
        {
            System.Console.WriteLine();
            WriteC("╭─ ", ConsoleColor.DarkGray);
            WriteC(Title, ConsoleColor.Cyan);
            WriteC(" " + new string('─', rightDashes) + "╮", ConsoleColor.DarkGray);
            System.Console.WriteLine();
            BannerRow("Mode        ", providerMode,           modeColor,           W);
            BannerRow("Environment ", environmentName,        ConsoleColor.Cyan,   W);
            BannerRow("Interval    ", $"{intervalSeconds}s",  ConsoleColor.Gray,   W);
            WriteC("╰" + new string('─', W) + "╯", ConsoleColor.DarkGray);
            System.Console.WriteLine();
            System.Console.WriteLine();
        }
    }

    // ── Cycle lines ───────────────────────────────────────────────────────────

    public static void WriteCycleStarted(DateTime utcNow, int customerCount, int intervalSeconds)
    {
        var localNow = utcNow.ToLocalTime();
        lock (_lock)
        {
            WriteC("▶", ConsoleColor.Cyan);
            System.Console.Write("  ");
            WriteC(localNow.ToString("dd.MM.yyyy HH:mm"), ConsoleColor.DarkGray);
            System.Console.Write("  ");
            WriteKv("customers", customerCount.ToString(), ConsoleColor.White);
            WriteKv("interval",  $"{intervalSeconds}s",   ConsoleColor.White, last: true);
            System.Console.WriteLine();
        }
    }

    public static void WriteCycleCompleted(
        int customerCount, int connectionCount,
        int fetched, int inserted, int updated, int failed, long elapsedMs)
    {
        var sepWidth = SafeWindowWidth();
        lock (_lock)
        {
            var topColor = failed > 0 ? ConsoleColor.Yellow : ConsoleColor.Green;
            WriteC("✓", topColor);
            System.Console.Write("  ");
            WriteKv("customers",   customerCount.ToString(),   ConsoleColor.White);
            WriteKv("connections", connectionCount.ToString(), ConsoleColor.White);
            WriteKvNum("fetched",  fetched,  ConsoleColor.Cyan);
            WriteKvNum("inserted", inserted, ConsoleColor.Green);
            WriteKvNum("updated",  updated,  ConsoleColor.Yellow);
            WriteKvNum("failed",   failed,   ConsoleColor.Red);
            WriteKv("elapsed", FormatElapsed(elapsedMs), ConsoleColor.DarkGray, last: true);
            System.Console.WriteLine();
            WriteC(new string('-', sepWidth), ConsoleColor.DarkGray);
            System.Console.WriteLine();
            System.Console.WriteLine();
        }
    }

    // ── Customer block ────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the customer block: header, separator, and either a disabled message
    /// or the per-connection table. Does nothing when sync is active and no connections ran.
    /// </summary>
    public static void WriteCustomerResult(
        string customerName,
        bool syncDisabled,
        IReadOnlyList<OrderSyncConnectionSummary> connections)
    {
        if (!syncDisabled && connections.Count == 0)
            return;

        lock (_lock)
        {
            System.Console.WriteLine();

            // Customer name + sync status on one line.
            System.Console.Write("  ");
            WriteCell(customerName, CustomerNameWidth, ConsoleColor.White);
            WriteC("Sync: ", ConsoleColor.DarkGray);
            WriteC(
                syncDisabled ? "Disabled" : "Active",
                syncDisabled ? ConsoleColor.Yellow : ConsoleColor.Green);
            System.Console.WriteLine();

            // Section separator.
            WriteC("  " + new string('-', CustomerSepLen), ConsoleColor.DarkGray);
            System.Console.WriteLine();

            if (syncDisabled)
            {
                WriteC("  Order sync is disabled for this tenant. Skipping provider sync.", ConsoleColor.DarkGray);
                System.Console.WriteLine();
                return;
            }

            // Table header + separator + data rows.
            WriteTableHeader();
            WriteTableSeparator();
            foreach (var c in connections)
                WriteConnectionRow(c);
        }
    }

    // ── Warnings / errors ─────────────────────────────────────────────────────

    public static void WriteWarning(string message)
    {
        lock (_lock)
        {
            WriteC("WARN ", ConsoleColor.Yellow);
            System.Console.WriteLine(message);
        }
    }

    public static void WriteError(string message)
    {
        lock (_lock)
        {
            WriteC("FAIL ", ConsoleColor.Red);
            System.Console.WriteLine(message);
        }
    }

    // ── Private table helpers ─────────────────────────────────────────────────

    private static void WriteTableHeader()
    {
        System.Console.Write("  ");
        WriteCell("Platform",  ColPlatform, ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteCell("Store",     ColStore,    ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteCell("Fetched",   ColFetched,  ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteCell("Inserted",  ColInserted, ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteCell("Updated",   ColUpdated,  ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteCell("Skipped",   ColSkipped,  ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteC("Time", ConsoleColor.DarkGray);
        System.Console.WriteLine();
    }

    private static void WriteTableSeparator()
    {
        System.Console.Write("  ");
        WriteC(new string('-', ColPlatform), ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteC(new string('-', ColStore),    ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteC(new string('-', ColFetched),  ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteC(new string('-', ColInserted), ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteC(new string('-', ColUpdated),  ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteC(new string('-', ColSkipped),  ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteC("------", ConsoleColor.DarkGray);
        System.Console.WriteLine();
    }

    private static void WriteConnectionRow(OrderSyncConnectionSummary c)
    {
        System.Console.Write("  ");
        WriteCell(c.Platform,  ColPlatform, ConsoleColor.Cyan);
        System.Console.Write("  ");
        WriteCell(c.StoreId,   ColStore,    ConsoleColor.Gray);
        System.Console.Write("  ");
        WriteCell(c.FetchedCount.ToString(),   ColFetched,
            c.FetchedCount  > 0 ? ConsoleColor.Cyan        : ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteCell(c.InsertedCount.ToString(),  ColInserted,
            c.InsertedCount > 0 ? ConsoleColor.Green       : ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteCell(c.UpdatedCount.ToString(),   ColUpdated,
            c.UpdatedCount  > 0 ? ConsoleColor.Yellow      : ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteCell(c.SkippedCount.ToString(),   ColSkipped,
            c.SkippedCount  > 0 ? ConsoleColor.DarkYellow  : ConsoleColor.DarkGray);
        System.Console.Write("  ");
        WriteC(FormatElapsed(c.ElapsedMs), ConsoleColor.DarkGray);
        if (c.IsFailed)
        {
            System.Console.Write("  ");
            WriteC("FAILED", ConsoleColor.Red);
        }
        System.Console.WriteLine();
    }

    // ── Private banner helper ─────────────────────────────────────────────────

    private static void BannerRow(string label, string value, ConsoleColor valueColor, int innerWidth)
    {
        int valueWidth = innerWidth - 2 - label.Length;
        WriteC("│", ConsoleColor.DarkGray);
        System.Console.Write("  " + label);
        WriteC(value.PadRight(valueWidth), valueColor);
        WriteC("│", ConsoleColor.DarkGray);
        System.Console.WriteLine();
    }

    // ── Private primitives ────────────────────────────────────────────────────

    private static void WriteCell(string value, int width, ConsoleColor color)
    {
        WriteC(value, color);
        if (value.Length < width)
            System.Console.Write(new string(' ', width - value.Length));
    }

    private static void WriteKv(string key, string value, ConsoleColor valueColor, bool last = false)
    {
        WriteC(key + "=", ConsoleColor.DarkGray);
        WriteC(value, valueColor);
        if (!last) System.Console.Write("  ");
    }

    private static void WriteKvNum(string key, int value, ConsoleColor positiveColor, bool last = false)
        => WriteKv(key, value.ToString(), value > 0 ? positiveColor : ConsoleColor.DarkGray, last);

    private static void WriteC(string text, ConsoleColor color)
    {
        System.Console.ForegroundColor = color;
        System.Console.Write(text);
        System.Console.ResetColor();
    }

    private static int SafeWindowWidth()
    {
        try
        {
            var w = System.Console.WindowWidth;
            return Math.Clamp(w > 0 ? w - 1 : 80, 60, 100);
        }
        catch
        {
            return 80;
        }
    }

    private static string FormatElapsed(long ms)
        => ms >= 1000 ? $"{ms / 1000.0:F1}s" : $"{ms}ms";
}
