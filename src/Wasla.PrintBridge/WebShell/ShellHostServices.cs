using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;

namespace Wasla.PrintBridge.WebShell;

/// <summary>Installed Windows printers as last discovered by the host. The page never supplies a printer list.</summary>
public interface IPrinterCatalog
{
    IReadOnlyList<string> Installed { get; }

    bool HasDiscovered { get; }

    Task<IReadOnlyList<string>> RefreshAsync(CancellationToken ct);

    bool IsInstalled(string printerName);
}

/// <summary>Persists the selected printer through the same settings store and validator as the classic window.</summary>
public interface IShellPrinterSettings
{
    bool TrySavePrinter(string printerName, out string? errorKey);
}

/// <summary>
/// Privileged desktop actions. They take no paths, URLs or credentials from the page: the host knows what to open.
/// </summary>
public interface IShellNativeActions
{
    void OpenClassicWindow();

    /// <summary>Opens the trusted native connection setup (server URL and device token are entered there only).</summary>
    void OpenConnectionSetup();

    /// <summary>Asks for explicit confirmation in a native dialog before the local token is cleared.</summary>
    bool ConfirmConnectionReset();

    /// <summary>Opens the known log folder in File Explorer; false when it does not exist yet.</summary>
    bool OpenLogFolder();
}

public sealed class WindowsPrinterCatalog : IPrinterCatalog
{
    private readonly Func<IReadOnlyList<string>> _enumerate;
    private IReadOnlyList<string> _installed = Array.Empty<string>();
    private int _discovered;

    public WindowsPrinterCatalog()
        : this(() => OperatingSystem.IsWindows() ? RawPrinterHelper.ListInstalledPrinters() : Array.Empty<string>())
    {
    }

    internal WindowsPrinterCatalog(Func<IReadOnlyList<string>> enumerate) => _enumerate = enumerate;

    public IReadOnlyList<string> Installed => Volatile.Read(ref _installed);

    public bool HasDiscovered => Volatile.Read(ref _discovered) == 1;

    public async Task<IReadOnlyList<string>> RefreshAsync(CancellationToken ct)
    {
        // Spooler enumeration can block on network printers; keep it off the UI thread.
        var printers = await Task.Run(_enumerate, ct).ConfigureAwait(false);
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

public sealed class ShellPrinterSettings : IShellPrinterSettings
{
    private readonly PrintBridgeSettingsStore _store;
    private readonly PrintBridgeSettingsHolder _holder;

    public ShellPrinterSettings(PrintBridgeSettingsStore store, PrintBridgeSettingsHolder holder)
    {
        _store = store;
        _holder = holder;
    }

    public bool TrySavePrinter(string printerName, out string? errorKey)
    {
        var (hub, bridge, ui) = _holder.Snapshot();

        // A new options object: a job already being printed keeps the printer it captured when it was claimed.
        var updated = MainForm.ClonePrintBridgeOptions(bridge);
        updated.PrinterMode = "WindowsPrinter";
        updated.PrinterName = printerName.Trim();

        if (!PrintBridgeSettingsValidator.TryValidatePrinterSettings(updated, out errorKey))
            return false;

        _store.Save(new PrintBridgeSettingsStore.AppSettingsDocument
        {
            OrderHub = hub,
            PrintBridge = updated,
            Ui = ui
        });
        _holder.Replace(hub, updated, ui);
        errorKey = null;
        return true;
    }
}

public static class ShellLogFolder
{
    /// <summary>The only folder the shell can open. No path is ever taken from the page.</summary>
    public static string Path => PrintBridgePaths.ProgramDataLogDirectory;

    public static bool TryOpen()
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
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
