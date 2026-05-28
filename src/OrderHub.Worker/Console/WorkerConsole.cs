using OrderHub.Application.Abstractions.Orders.Services;

namespace OrderHub.Worker.Console;

/// <summary>
/// Lightweight Development-only console presentation layer.
/// No business logic, no database access, no provider calls — formatting only.
/// </summary>
internal static class WorkerConsole
{
    // Column widths for the per-connection table.
    private const int ColPlatform = 15;
    private const int ColStore    = 10;
    private const int ColNum      = 8;  // Fetched / Inserted / Updated / Skipped

    private static readonly object _lock = new();

    // ── Startup banner ────────────────────────────────────────────────────────

    public static void WriteStartupBanner(string providerMode, string environmentName, int intervalSeconds)
    {
        const int W = 48;              // inner content width (between ╭ and ╮)
        const string Title = "OrderHub Worker";
        int rightDashes = W - Title.Length - 3; // "─ " + Title + " " uses 3 + Title chars

        var modeColor = providerMode.Equals("Real", StringComparison.OrdinalIgnoreCase)
            ? ConsoleColor.Yellow
            : ConsoleColor.Green;

        lock (_lock)
        {
            System.Console.WriteLine();

            // ╭─ OrderHub Worker ───...───╮
            WriteC("╭─ ", ConsoleColor.DarkGray);
            WriteC(Title, ConsoleColor.Cyan);
            WriteC(" " + new string('─', rightDashes) + "╮", ConsoleColor.DarkGray);
            System.Console.WriteLine();

            BannerRow("Mode        ", providerMode,        modeColor,            W);
            BannerRow("Environment ", environmentName,     ConsoleColor.Cyan,    W);
            BannerRow("Interval    ", $"{intervalSeconds}s", ConsoleColor.Gray,  W);

            // ╰────...────╯
            WriteC("╰" + new string('─', W) + "╯", ConsoleColor.DarkGray);
            System.Console.WriteLine();
            System.Console.WriteLine();
        }
    }

    // ── Cycle lines ───────────────────────────────────────────────────────────

    public static void WriteCycleStarted(DateTime utcNow, int customerCount, int intervalSeconds)
    {
        lock (_lock)
        {
            WriteC("▶", ConsoleColor.Cyan);
            System.Console.Write("  ");
            WriteC(utcNow.ToString("HH:mm:ss"), ConsoleColor.DarkGray);
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
            System.Console.WriteLine();
        }
    }

    // ── Customer / connection table ───────────────────────────────────────────

    public static void WriteCustomerResult(string customerName, IReadOnlyList<OrderSyncConnectionSummary> connections)
    {
        if (connections.Count == 0)
            return;

        lock (_lock)
        {
            System.Console.WriteLine();

            // Customer name
            System.Console.Write("  ");
            WriteC(customerName, ConsoleColor.White);
            System.Console.WriteLine();

            // Header row
            WriteTableRow(
                "Platform", "Store", "Fetched", "Inserted", "Updated", "Skipped", "Time",
                ConsoleColor.DarkGray, ConsoleColor.DarkGray,
                ConsoleColor.DarkGray, ConsoleColor.DarkGray,
                ConsoleColor.DarkGray, ConsoleColor.DarkGray,
                ConsoleColor.DarkGray);

            // Separator
            WriteC(
                "  " + new string('─', ColPlatform) +
                "  " + new string('─', ColStore) +
                "  " + new string('─', ColNum) +
                "  " + new string('─', ColNum) +
                "  " + new string('─', ColNum) +
                "  " + new string('─', ColNum) +
                "  " + new string('─', 7),
                ConsoleColor.DarkGray);
            System.Console.WriteLine();

            // Data rows
            foreach (var c in connections)
            {
                var failedColor = c.IsFailed ? ConsoleColor.Red : ConsoleColor.DarkGray;

                WriteTableRow(
                    c.Platform,           c.StoreId,
                    c.FetchedCount.ToString(),   c.InsertedCount.ToString(),
                    c.UpdatedCount.ToString(),   c.SkippedCount.ToString(),
                    FormatElapsed(c.ElapsedMs),
                    ConsoleColor.Cyan,    ConsoleColor.Gray,
                    c.FetchedCount  > 0 ? ConsoleColor.Cyan   : ConsoleColor.DarkGray,
                    c.InsertedCount > 0 ? ConsoleColor.Green  : ConsoleColor.DarkGray,
                    c.UpdatedCount  > 0 ? ConsoleColor.Yellow : ConsoleColor.DarkGray,
                    c.SkippedCount  > 0 ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray,
                    ConsoleColor.DarkGray);

                if (c.IsFailed)
                {
                    System.Console.Write("  ");
                    WriteC("FAILED", ConsoleColor.Red);
                }

                System.Console.WriteLine();
            }
        }
    }

    public static void WriteSyncDisabled(string customerName, string slug)
    {
        lock (_lock)
        {
            System.Console.Write("  ");
            WriteC("○ ", ConsoleColor.DarkGray);
            WriteC($"{customerName} ({slug}) — sync disabled", ConsoleColor.DarkGray);
            System.Console.WriteLine();
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

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void BannerRow(string label, string value, ConsoleColor valueColor, int innerWidth)
    {
        // │  [label(12)][value padded to innerWidth - 2 - label.Length]│
        int valueWidth = innerWidth - 2 - label.Length;
        WriteC("│", ConsoleColor.DarkGray);
        System.Console.Write("  " + label);
        WriteC(value.PadRight(valueWidth), valueColor);
        WriteC("│", ConsoleColor.DarkGray);
        System.Console.WriteLine();
    }

    private static void WriteTableRow(
        string col0, string col1, string col2, string col3, string col4, string col5, string col6,
        ConsoleColor c0, ConsoleColor c1, ConsoleColor c2, ConsoleColor c3,
        ConsoleColor c4, ConsoleColor c5, ConsoleColor c6)
    {
        System.Console.Write("  ");
        WriteCell(col0, ColPlatform, c0);
        System.Console.Write("  ");
        WriteCell(col1, ColStore,    c1);
        System.Console.Write("  ");
        WriteCell(col2, ColNum,      c2);
        System.Console.Write("  ");
        WriteCell(col3, ColNum,      c3);
        System.Console.Write("  ");
        WriteCell(col4, ColNum,      c4);
        System.Console.Write("  ");
        WriteCell(col5, ColNum,      c5);
        System.Console.Write("  ");
        WriteC(col6, c6);
    }

    private static void WriteCell(string value, int width, ConsoleColor color)
    {
        WriteC(value, color);
        // Padding in default color so only the text is colored, not trailing spaces.
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

    private static string FormatElapsed(long ms)
        => ms >= 1000 ? $"{ms / 1000.0:F1}s" : $"{ms}ms";
}
