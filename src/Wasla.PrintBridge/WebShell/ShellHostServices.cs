using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.WebShell;

/// <summary>Installed Windows printers as last discovered by the host. The page never supplies a printer list.</summary>
public interface IPrinterCatalog
{
    IReadOnlyList<string> Installed { get; }

    bool HasDiscovered { get; }

    Task<IReadOnlyList<string>> RefreshAsync(CancellationToken ct);

    bool IsInstalled(string printerName);
}

/// <summary>Saves the selected printer through the engine, which validates it (WAS-61: in the service).</summary>
public interface IShellPrinterSettings
{
    /// <summary>Null when saved, otherwise the resource key of the reason.</summary>
    Task<string?> SavePrinterAsync(string printerName, CancellationToken ct);
}

/// <summary>The operational settings the app may change: test mode and the idle, busy and error poll intervals.</summary>
public sealed record ShellOperationalSettingsChange(bool TestMode, int IdlePollSeconds, int BusyPollSeconds, int ErrorPollSeconds);

/// <summary>Saves operational settings through the engine, which applies <c>PrintBridgeSettingsValidator</c>.</summary>
public interface IShellOperationalSettings
{
    /// <summary>Null when saved, otherwise the resource key of the reason.</summary>
    Task<string?> SaveAsync(ShellOperationalSettingsChange change, CancellationToken ct);
}

/// <summary>
/// Privileged desktop actions. They take no paths, URLs or credentials from the page: the host knows what to open,
/// and every confirmation is asked in a native dialog the page cannot answer itself.
/// </summary>
public interface IShellNativeActions
{
    /// <summary>
    /// Shows the native connection dialog, where the server URL and device token are entered, and completes when it
    /// closes. The result carries only a localized message.
    /// </summary>
    Task<ShellConnectionSetupResult> RunConnectionSetupAsync();

    /// <summary>Asks for explicit confirmation in a native dialog before the local token is cleared.</summary>
    Task<bool> ConfirmConnectionResetAsync();

    /// <summary>Asks for explicit confirmation in a native dialog before test mode stops paper printing.</summary>
    Task<bool> ConfirmEnableTestModeAsync();

    /// <summary>Opens the known log folder in File Explorer; false when it does not exist yet.</summary>
    bool OpenLogFolder();
}

/// <summary>
/// The printers the engine can use, as the service lists them (WAS-61). The service runs in session 0 under its own
/// account, so a printer a user added only to their own profile is not in this list; only these can be saved.
/// </summary>
public sealed class EnginePrinterCatalog : IPrinterCatalog
{
    private readonly IPrintBridgeEngine _engine;
    private IReadOnlyList<string> _installed = Array.Empty<string>();
    private int _discovered;

    public EnginePrinterCatalog(IPrintBridgeEngine engine) => _engine = engine;

    public IReadOnlyList<string> Installed => Volatile.Read(ref _installed);

    public bool HasDiscovered => Volatile.Read(ref _discovered) == 1;

    public async Task<IReadOnlyList<string>> RefreshAsync(CancellationToken ct)
    {
        var printers = await _engine.ListPrintersAsync(ct).ConfigureAwait(false);
        var ordered = printers
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        Volatile.Write(ref _installed, ordered);
        Volatile.Write(ref _discovered, 1);
        return ordered;
    }

    public bool IsInstalled(string printerName) =>
        Installed.Any(p => string.Equals(p, printerName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Saves the printer through the engine (WAS-61: the service writes the settings file).</summary>
public sealed class EnginePrinterSettings(IPrintBridgeEngine engine) : IShellPrinterSettings
{
    public Task<string?> SavePrinterAsync(string printerName, CancellationToken ct) => engine.SavePrinterAsync(printerName, ct);
}

/// <summary>
/// Saves test mode and the poll intervals through the engine. The engine reads these values for every poll and job, so
/// they apply from its next check without a restart (a wait already in progress finishes first).
/// </summary>
public sealed class EngineOperationalSettings(IPrintBridgeEngine engine) : IShellOperationalSettings
{
    public Task<string?> SaveAsync(ShellOperationalSettingsChange change, CancellationToken ct) =>
        engine.SaveOperationalSettingsAsync(
            new PrintBridgeOperationalSettings(change.TestMode, change.IdlePollSeconds, change.BusyPollSeconds, change.ErrorPollSeconds),
            ct);
}

public static class ShellLogFolder
{
    /// <summary>
    /// The only folder the shell can open: the service's log folder, which local users may read (WAS-61). No path is
    /// ever taken from the page, and the app never creates the service's folders.
    /// </summary>
    public static string Path => PrintBridgePaths.ProgramDataLogDirectory;

    public static bool TryOpen()
    {
        if (!Directory.Exists(Path))
            return false;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path,
            UseShellExecute = true
        });
        return true;
    }
}
