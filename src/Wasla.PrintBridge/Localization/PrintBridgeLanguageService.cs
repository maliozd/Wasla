using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Localization;

/// <summary>
/// Applies a UI language chosen in the WebView2 shell with the same steps as the classic Settings tab:
/// persist <c>Ui.Language</c>, update the in-memory settings, then switch the culture (which notifies
/// every open window and the tray). Must be awaited on the UI thread.
/// </summary>
public sealed class PrintBridgeLanguageService : IShellLanguageSwitcher
{
    private readonly PrintBridgeSettingsStore _store;
    private readonly PrintBridgeSettingsHolder _holder;
    private readonly PrintBridgeCultureService _cultureService;
    private readonly ILogger<PrintBridgeLanguageService> _logger;

    public PrintBridgeLanguageService(
        PrintBridgeSettingsStore store,
        PrintBridgeSettingsHolder holder,
        PrintBridgeCultureService cultureService,
        ILogger<PrintBridgeLanguageService> logger)
    {
        _store = store;
        _holder = holder;
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
            await Task.Run(() => _store.SaveLanguage(culture)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Language setting could not be saved.");
            return false;
        }

        var ui = MainForm.CloneUiOptions(_holder.Ui);
        ui.Language = culture;
        _holder.ReplaceUi(ui);
        _cultureService.Initialize(culture);
        return true;
    }
}
