using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;

namespace Wasla.PrintBridge.WebShell;

/// <summary>Why the connection dialog is shown; decides its title, introduction and token rules.</summary>
public enum ShellConnectionSetupMode
{
    /// <summary>No device token is saved: the bridge has never been connected, or it was reset in an earlier session.</summary>
    FirstSetup,

    /// <summary>The saved token was rejected or reset; a new token is required.</summary>
    Reconnect,

    /// <summary>A token is saved and accepted; the user changes the server URL or the token.</summary>
    Change
}

/// <summary>The input a validation or verification message belongs to, so the dialog can focus it.</summary>
public enum ShellConnectionSetupField
{
    None,
    ServerUrl,
    Token
}

public enum ShellConnectionSetupOutcome
{
    /// <summary>Verified, saved and active.</summary>
    Connected,

    /// <summary>Verified, saved and active, but printing cannot start until a printer is chosen.</summary>
    ConnectedChoosePrinter,

    /// <summary>The input failed local validation. Nothing was sent or saved.</summary>
    Invalid,

    /// <summary>The server did not accept the candidate or could not be reached. Nothing was saved.</summary>
    VerificationFailed,

    /// <summary>Verified, but the settings file could not be written. Nothing changed.</summary>
    SaveFailed,

    /// <summary>Verified, but a receipt was being printed, so nothing was saved; the user can retry when it is done.</summary>
    PrintingInProgress,

    /// <summary>Closed or abandoned before anything was saved.</summary>
    Cancelled
}

/// <param name="Message">Localized and sanitized: never exception text, a server response, the URL or the token.</param>
public sealed record ShellConnectionSetupResult(
    ShellConnectionSetupOutcome Outcome,
    string Message,
    ShellConnectionSetupField Field = ShellConnectionSetupField.None)
{
    public bool IsConnected => Outcome is ShellConnectionSetupOutcome.Connected or ShellConnectionSetupOutcome.ConnectedChoosePrinter;
}

/// <summary>
/// The logic behind the native connection dialog of the WebView2 app. A new server URL and token are validated with
/// the existing rules, verified against the server without being saved, and only then saved and activated through the
/// engine (<see cref="IPrintBridgeEngine.ApplyVerifiedConnectionAsync"/>), which uses the existing settings store. A
/// failed check or a cancel leaves the saved settings untouched. The saved token is never returned: an empty token
/// input keeps it only when the bridge is already connected (<see cref="ShellConnectionSetupMode.Change"/>).
/// </summary>
public sealed class ShellConnectionSetup
{
    public const int MaxServerUrlLength = 2048;
    public const int MaxTokenLength = 512;
    public static readonly TimeSpan DefaultVerificationTimeout = TimeSpan.FromSeconds(30);

    private readonly IPrintBridgeEngine _engine;
    private readonly PrintBridgeSettingsHolder _settings;
    private readonly PrintBridgeLocalizer _localizer;
    private readonly ILogger _logger;
    private readonly CancellationToken _appLifetime;
    private readonly TimeSpan _timeout;

    public ShellConnectionSetup(
        IPrintBridgeEngine engine,
        PrintBridgeSettingsHolder settings,
        PrintBridgeLocalizer localizer,
        ILogger logger,
        CancellationToken appLifetime,
        TimeSpan? verificationTimeout = null)
    {
        _engine = engine;
        _settings = settings;
        _localizer = localizer;
        _logger = logger;
        _appLifetime = appLifetime;
        _timeout = verificationTimeout ?? DefaultVerificationTimeout;
    }

    public ShellConnectionSetupMode Mode
    {
        get
        {
            var issue = _engine.GetStatus().LastIssue;
            if (issue?.Code is PrintBridgeRuntimeIssueCode.ReconnectRequired or PrintBridgeRuntimeIssueCode.DuplicateInstallation)
                return ShellConnectionSetupMode.Reconnect;

            return HasSavedToken ? ShellConnectionSetupMode.Change : ShellConnectionSetupMode.FirstSetup;
        }
    }

    /// <summary>Whether an empty token input keeps the saved token. The token itself is never exposed.</summary>
    public bool CanKeepSavedToken => Mode == ShellConnectionSetupMode.Change;

    /// <summary>
    /// The saved server URL to prefill in the native dialog, or empty for the built-in placeholder default. The
    /// native dialog may show it; the WebView2 page never receives it.
    /// </summary>
    public string SavedServerUrl
    {
        get
        {
            var url = _settings.OrderHub.ServerUrl?.Trim() ?? string.Empty;
            return string.Equals(url, WaslaOptions.DefaultServerUrl, StringComparison.OrdinalIgnoreCase) ? string.Empty : url;
        }
    }

    private bool HasSavedToken => !string.IsNullOrWhiteSpace(_settings.OrderHub.AgentToken);

    /// <summary>
    /// Validates the input with the existing connection rules and the setup-link paste guard. No request is sent.
    /// </summary>
    public ShellConnectionSetupResult? Validate(string? serverUrl, string? tokenInput, out WaslaOptions? candidate)
    {
        candidate = null;
        var url = serverUrl?.Trim() ?? string.Empty;
        var token = tokenInput?.Trim() ?? string.Empty;

        if (url.Length > MaxServerUrlLength)
            return Invalid("Validation.InvalidServerUrl", ShellConnectionSetupField.ServerUrl);

        if (token.Length > MaxTokenLength)
            return Invalid("Shell.Setup.TokenTooLong", ShellConnectionSetupField.Token);

        if (PrintBridgeTokenPasteGuard.LooksLikeSetupOrProtocolValue(token))
            return Invalid("Settings.AgentTokenPasteGuard", ShellConnectionSetupField.Token);

        if (token.Length == 0 && CanKeepSavedToken)
            token = _settings.OrderHub.AgentToken.Trim();

        var options = new WaslaOptions { ServerUrl = url, AgentToken = token };
        if (!PrintBridgeSettingsValidator.TryValidateConnectionSettings(options, out var errorKey))
        {
            var field = errorKey == "Validation.AgentTokenRequired" ? ShellConnectionSetupField.Token : ShellConnectionSetupField.ServerUrl;
            return Invalid(errorKey!, field);
        }

        candidate = options;
        return null;
    }

    /// <summary>
    /// Validates, verifies and then saves the connection. <paramref name="ct"/> is the dialog's cancel: it stops
    /// verification and is honored until saving begins. <paramref name="saving"/> is called right before the saved
    /// settings change, after which the dialog must not close until this method returns.
    /// </summary>
    public async Task<ShellConnectionSetupResult> ConnectAsync(
        string? serverUrl,
        string? tokenInput,
        CancellationToken ct,
        Action? saving = null)
    {
        var invalid = Validate(serverUrl, tokenInput, out var candidate);
        if (invalid is not null)
            return invalid;

        var startListening = Mode != ShellConnectionSetupMode.Change;
        WaslaPrintBridgeClient.PrintBridgeHealthResult health;
        using (var verification = CancellationTokenSource.CreateLinkedTokenSource(ct, _appLifetime))
        {
            verification.CancelAfter(_timeout);
            try
            {
                health = await _engine.CheckConnectionAsync(candidate!, verification.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _appLifetime.IsCancellationRequested)
            {
                return Cancelled();
            }
            catch (Exception ex)
            {
                // The client already logged the request failure; only the category is recorded here.
                _logger.LogWarning("Print Bridge connection setup could not verify the new connection. Reason={Reason}", ReasonOf(ex));
                return DescribeVerificationFailure(ex);
            }
        }

        if (ct.IsCancellationRequested || _appLifetime.IsCancellationRequested)
            return Cancelled();

        saving?.Invoke();
        PrintBridgeConnectionChange change;
        try
        {
            change = await _engine.ApplyVerifiedConnectionAsync(candidate!, health, startListening, _appLifetime).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Print Bridge connection setup could not save the verified connection.");
            return new ShellConnectionSetupResult(ShellConnectionSetupOutcome.SaveFailed, _localizer["Shell.Setup.SaveFailed"]);
        }

        _logger.LogInformation("Print Bridge connection setup finished. Result={Result}", change);
        return change switch
        {
            PrintBridgeConnectionChange.SaveFailed =>
                new ShellConnectionSetupResult(ShellConnectionSetupOutcome.SaveFailed, _localizer["Shell.Setup.SaveFailed"]),
            PrintBridgeConnectionChange.Abandoned => Cancelled(),
            PrintBridgeConnectionChange.PrintingInProgress =>
                new ShellConnectionSetupResult(ShellConnectionSetupOutcome.PrintingInProgress, _localizer["Shell.Setup.PrintingInProgress"]),
            PrintBridgeConnectionChange.AppliedNotListening when !CanPrint() =>
                new ShellConnectionSetupResult(ShellConnectionSetupOutcome.ConnectedChoosePrinter, _localizer["Shell.Setup.ConnectedChoosePrinter"]),
            _ => new ShellConnectionSetupResult(ShellConnectionSetupOutcome.Connected, _localizer["Shell.Setup.Connected"])
        };
    }

    /// <summary>The engine's own start check for the printer (test mode does not make a missing printer usable).</summary>
    private bool CanPrint() =>
        PrintBridgeSettingsValidator.TryValidatePrinterAvailability(_settings.Bridge.Clone(), out _);

    public ShellConnectionSetupResult Cancelled() =>
        new(ShellConnectionSetupOutcome.Cancelled, _localizer["Shell.Setup.Cancelled"]);

    /// <summary>
    /// The dialog was closed because a setup link took over. Nothing was saved by the dialog, and it reports no message:
    /// the link reports the outcome, so the page never says "not changed" after the link changed the connection.
    /// </summary>
    public ShellConnectionSetupResult Superseded() =>
        new(ShellConnectionSetupOutcome.Cancelled, string.Empty);

    private ShellConnectionSetupResult Invalid(string key, ShellConnectionSetupField field) =>
        new(ShellConnectionSetupOutcome.Invalid, _localizer[key], field);

    /// <summary>Localized, actionable and free of server text. Rejected tokens point to the token field.</summary>
    private ShellConnectionSetupResult DescribeVerificationFailure(Exception ex)
    {
        if (ex is OperationCanceledException)
            return new ShellConnectionSetupResult(ShellConnectionSetupOutcome.VerificationFailed, _localizer["Shell.Setup.TimedOut"], ShellConnectionSetupField.ServerUrl);

        if (ex is not PrintBridgeConnectionException connection)
            return new ShellConnectionSetupResult(ShellConnectionSetupOutcome.VerificationFailed, _localizer["Shell.Op.Failed"]);

        var issue = new PrintBridgeRuntimeIssue(connection.IssueCode, connection.UserMessageKey, connection.FormatArgs);
        return connection.IssueCode switch
        {
            PrintBridgeRuntimeIssueCode.ReconnectRequired => new(
                ShellConnectionSetupOutcome.VerificationFailed, _localizer["Shell.Setup.TokenRejected"], ShellConnectionSetupField.Token),
            PrintBridgeRuntimeIssueCode.ServerUnreachable
                or PrintBridgeRuntimeIssueCode.SslError
                or PrintBridgeRuntimeIssueCode.EndpointNotFound => new(
                ShellConnectionSetupOutcome.VerificationFailed, _localizer.GetRuntimeIssueDetail(issue), ShellConnectionSetupField.ServerUrl),
            _ => new(ShellConnectionSetupOutcome.VerificationFailed, _localizer.GetRuntimeIssueDetail(issue))
        };
    }

    private static string ReasonOf(Exception ex) => ex switch
    {
        PrintBridgeConnectionException connection => connection.IssueCode.ToString(),
        OperationCanceledException => "Timeout",
        _ => ex.GetType().Name
    };
}
