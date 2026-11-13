using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Localization;

/// <summary>
/// Applies a UI language chosen in the WebView2 app: the engine saves <c>Ui.Language</c> (WAS-61: the service owns the
/// settings file), then the culture switches, which notifies the app window and the tray. Must be awaited on the UI thread.
/// </summary>
public sealed class PrintBridgeLanguageService : IShellLanguageSwitcher
{
    private readonly IPrintBridgeEngine _engine;
    private readonly PrintBridgeCultureService _cultureService;
    private readonly ILogger<PrintBridgeLanguageService> _logger;

    public PrintBridgeLanguageService(
        IPrintBridgeEngine engine,
        PrintBridgeCultureService cultureService,
        ILogger<PrintBridgeLanguageService> logger)
    {
        _engine = engine;
        _cultureService = cultureService;
        _logger = logger;
    }

    public async Task<bool> ChangeAsync(string culture)
    {
        if (!SupportedCultures.IsExactSupportedName(culture))
            return false;

        if (string.Equals(_cultureService.CurrentCulture.Name, culture, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            using var timeout = new CancellationTokenSource(ShellOperations.DefaultTimeout);
            await _engine.SaveLanguageAsync(culture, timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Language setting could not be saved: {Error}", ex.GetType().Name);
            return false;
        }

        _cultureService.Initialize(culture);
        return true;
    }
}
