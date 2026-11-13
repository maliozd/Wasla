using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.UI;

/// <summary>
/// The tray application. Its one window is the WebView2 app (<see cref="PrintBridgeShellForm"/>): tray Open,
/// double-click, Print history, Settings, setup links and a first run without a usable connection all open it. When
/// the WebView2 Runtime is missing, too old or fails to start, the tray shows <see cref="WebView2RuntimeNotice"/>
/// instead; the tray keeps running.
/// <para>
/// WAS-61: the engine runs in the Print Bridge service. The tray is a client of it (<see cref="IPrintBridgeEngine"/>):
/// closing, exiting, crashing or restarting the app never stops printing or acknowledgement. Only the explicit Stop in
/// the app stops printing.
/// </para>
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly PrintBridgeUiServices _services;

    private readonly IPrintBridgeEngine _engine;

    private readonly PrintBridgeLocalizer _localizer;

    private readonly PrintBridgeCultureService _cultureService;

    private readonly NotifyIcon _trayIcon;

    /// <summary>1 while this app applies a setup link; the service refuses a second one meanwhile.</summary>
    private int _autoSetupRunning;

    /// <summary>Set once the first state from the service decided whether the first-run window opens.</summary>
    private bool _firstStateHandled;

    private readonly ContextMenuStrip _trayMenu;

    private readonly ToolStripMenuItem _connectionMenuItem;

    private readonly ToolStripMenuItem _openMenuItem;

    private readonly ToolStripMenuItem _printHistoryMenuItem;

    private readonly ToolStripMenuItem _printerTestMenuItem;

    private readonly ToolStripMenuItem _settingsMenuItem;

    private readonly ToolStripMenuItem _exitMenuItem;

    private readonly IWebView2RuntimeProbe _webViewRuntimeProbe;

    private readonly IPrinterCatalog _printerCatalog;

    /// <summary>Cancelled on Exit, so a connection change still in progress is abandoned instead of half-applied.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>The UI thread's context; tray updates raised on engine threads are queued here.</summary>
    private readonly SynchronizationContext _uiContext;

    private readonly int _uiThreadId;

    private readonly ILogger _logger;

    /// <summary>
    /// 0 while running, 1 from the first Exit request on (WAS-59). Set once, atomically, before anything is disposed;
    /// every entry point checks it, so nothing new starts once the application is shutting down.
    /// </summary>
    private int _shutdownRequested;

    /// <summary>Completes when the one shutdown sequence has finished. Every Exit request returns it.</summary>
    private readonly TaskCompletionSource _shutdownCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<string> _shutdownSteps = [];

    private PrintBridgeShellForm? _shellForm;

    /// <summary>Set when the app window failed to start; the runtime notice is shown for the rest of the session.</summary>
    private bool _shellFailedThisSession;

    private WebView2RuntimeNotice? _runtimeNotice;

    public TrayApplicationContext(PrintBridgeUiServices services)
        : this(services, new WebView2RuntimeProbe())
    {
    }

    /// <summary>For tests: a runtime probe that can report the WebView2 Runtime as missing.</summary>
    internal TrayApplicationContext(PrintBridgeUiServices services, IWebView2RuntimeProbe webViewRuntimeProbe)
    {
        _services = services;
        _webViewRuntimeProbe = webViewRuntimeProbe;
        _engine = services.Engine;
        _localizer = services.Localizer;
        _cultureService = services.Culture;
        _printerCatalog = new EnginePrinterCatalog(_engine);
        _logger = services.Loggers.CreateLogger("Wasla.PrintBridge.Tray");

        // The menu is the first control created here; creating it installs the Windows Forms context on this thread,
        // the UI thread.
        _trayMenu = new ContextMenuStrip();
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("The tray application must be created on the UI thread.");
        _uiThreadId = Environment.CurrentManagedThreadId;

        _connectionMenuItem = new ToolStripMenuItem { Enabled = false };
        _openMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowPrimaryWindow());
        _printHistoryMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowPrintHistory());
        _printerTestMenuItem = new ToolStripMenuItem(string.Empty, null, OnPrinterTestClicked);
        _settingsMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowSettings());
        _exitMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => _ = ExitAsync());

        _trayMenu.Items.Add(_connectionMenuItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(_openMenuItem);
        _trayMenu.Items.Add(_printHistoryMenuItem);
        _trayMenu.Items.Add(_printerTestMenuItem);
        _trayMenu.Items.Add(_settingsMenuItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(_exitMenuItem);

        _trayIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(TrayIconState.ConnectionLost),
            Text = PrintBridgePaths.ProductDisplayName,
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => ShowPrimaryWindow();

        _engine.StatusChanged += OnRuntimeStatusChanged;
        _engine.BackgroundSetupCompleted += OnBackgroundSetupCompleted;
        _cultureService.CultureChanged += OnCultureChanged;
        ApplyTrayLocalization();
        UpdateTrayMenu();

        // The service starts printing on its own and finishes a pending move (WAS-91); the app only shows the result.
        // First run (or a connection that was reset): once the service's first state shows that no connection is saved,
        // the app window opens on Overview, whose next step is connecting. Queued, so it runs once the message loop does.
        _uiContext.Post(_ => HandleFirstState(), null);
    }

    private void ApplyTrayLocalization()
    {
        _openMenuItem.Text = _localizer["Tray.Open"];
        _printHistoryMenuItem.Text = _localizer["Tray.PrintHistory"];
        _printerTestMenuItem.Text = _localizer["Tray.PrinterTest"];
        _settingsMenuItem.Text = _localizer["Tray.Settings"];
        _exitMenuItem.Text = _localizer["Tray.Exit"];
        _trayMenu.RightToLeft = _cultureService.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;
    }

    /// <summary>
    /// Once, on the UI thread, when the service's state is known: opens the app window when no connection is saved. While
    /// the service cannot be reached the decision waits; the tray shows that the service is unavailable.
    /// </summary>
    private void HandleFirstState()
    {
        if (_firstStateHandled || IsShuttingDown)
            return;

        if (_engine.GetStatus().LastIssue is { IsServiceIssue: true })
            return;

        _firstStateHandled = true;
        if (!_engine.Settings.HasSavedToken)
            ShowPrimaryWindow();
    }

    /// <summary>The language saved in the service is the app's language; a change made in another app follows here.</summary>
    private void ApplySavedLanguage()
    {
        var saved = _engine.Settings.Ui.Language;
        if (SupportedCultures.IsExactSupportedName(saved)
            && !string.Equals(saved, _cultureService.CurrentCulture.Name, StringComparison.OrdinalIgnoreCase))
        {
            _cultureService.Initialize(saved);
        }
    }

    /// <summary>Tray "Open", double-click and the first run.</summary>
    private void ShowPrimaryWindow() => ShowAppWindow(tab: null);

    private void ShowPrintHistory() => ShowAppWindow("history");

    private void ShowSettings() => ShowAppWindow("settings");

    /// <summary>
    /// Every tray entry point goes through here: the one WebView2 window is shown (or brought forward) on
    /// <paramref name="tab"/>, or the runtime notice when the WebView2 Runtime cannot be used.
    /// </summary>
    private void ShowAppWindow(string? tab)
    {
        if (IsShuttingDown)
            return;

        if (!TryShowShell(tab))
            ShowRuntimeNotice();
    }

    private bool TryShowShell(string? tab)
    {
        if (!IsShellUsable())
            return false;

        GetOrCreateShellForm().ShowShell(tab);
        return true;
    }

    private bool IsShellUsable()
    {
        if (_shellFailedThisSession)
            return false;

        if (_shellForm is { IsDisposed: false })
            return true;

        if (_webViewRuntimeProbe.Probe().IsAvailable)
            return true;

        _logger.LogWarning("The WebView2 Runtime is missing or too old; showing the runtime notice instead of the app window.");
        return false;
    }

    private PrintBridgeShellForm GetOrCreateShellForm()
    {
        if (_shellForm is { IsDisposed: false })
            return _shellForm;

        var loggers = _services.Loggers;
        var logger = loggers.CreateLogger("Wasla.PrintBridge.Shell");
        _shellForm = new PrintBridgeShellForm(
            _engine,
            _engine.Settings,
            _printerCatalog,
            new EnginePrinterSettings(_engine),
            new EngineOperationalSettings(_engine),
            new ShellConnectionSetup(_engine, _engine.Settings, _localizer, logger, _lifetime.Token),
            _localizer,
            _cultureService,
            new PrintBridgeLanguageService(_engine, _cultureService, loggers.CreateLogger<PrintBridgeLanguageService>()),
            _webViewRuntimeProbe.Probe().Version,
            logger);
        _shellForm.ShellUnavailable += OnShellUnavailable;
        ShellFormCreatedForTests?.Invoke(_shellForm);
        return _shellForm;
    }

    private void OnShellUnavailable(object? sender, EventArgs e)
    {
        if (IsShuttingDown)
            return;

        _shellFailedThisSession = true;
        if (sender is PrintBridgeShellForm failed)
        {
            failed.ShellUnavailable -= OnShellUnavailable;
            if (ReferenceEquals(_shellForm, failed))
                _shellForm = null;
            failed.BeginInvoke(failed.Dispose);
        }

        ShowRuntimeNotice();
    }

    /// <summary>Shows the one runtime notice, or brings it forward when it is already open.</summary>
    private void ShowRuntimeNotice()
    {
        if (IsShuttingDown)
            return;

        if (_runtimeNotice is not { IsDisposed: false } notice)
        {
            notice = new WebView2RuntimeNotice(
                _localizer["RuntimeNotice.Title"],
                _localizer["RuntimeNotice.Message"],
                _localizer["RuntimeNotice.Close"],
                _cultureService.IsRightToLeft,
                ShellWindowTheme.IsWindowsAppDarkModeEnabled());
            notice.FormClosed += OnRuntimeNoticeClosed;
            _runtimeNotice = notice;
            RuntimeNoticeCreatedForTests?.Invoke(notice);
        }

        notice.Show();
        if (notice.WindowState == FormWindowState.Minimized)
            notice.WindowState = FormWindowState.Normal;
        notice.Activate();
    }

    /// <summary>A closed modeless window disposes itself; the next request creates a new notice.</summary>
    private void OnRuntimeNoticeClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is WebView2RuntimeNotice closed)
        {
            closed.FormClosed -= OnRuntimeNoticeClosed;
            if (ReferenceEquals(_runtimeNotice, closed))
                _runtimeNotice = null;
        }
    }

    private async void OnPrinterTestClicked(object? sender, EventArgs e)
    {
        if (IsShuttingDown)
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _engine.TestPrinterAsync(cts.Token).ConfigureAwait(true);
            MessageBox.Show(
                _localizer["Message.TestPrintSent"],
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                GetUserErrorMessage(ex),
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OnRuntimeStatusChanged(object? sender, EventArgs e) => UpdateTrayMenu();

    /// <summary>
    /// A move confirmed earlier was finished by the service in the background (WAS-91): shown in the app window when it is
    /// open, never as an unprompted message box.
    /// </summary>
    private void OnBackgroundSetupCompleted(object? sender, PrintBridgeAutoSetupOutcome outcome)
    {
        if (IsShuttingDown)
            return;

        try
        {
            _uiContext.Post(_ =>
            {
                if (IsShuttingDown || outcome == PrintBridgeAutoSetupOutcome.ChangePending)
                    return;

                UpdateTrayMenu();
                if (_shellForm is { IsDisposed: false, Visible: true } shell)
                    ReportAutoSetupOutcome(shell, outcome);
            }, null);
        }
        catch (InvalidOperationException) when (IsShuttingDown)
        {
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e) => UpdateTrayMenu();

    /// <summary>
    /// Brings the tray icon, tooltip and menu up to date. Raised on engine threads too, so the update always runs on the
    /// UI thread (never on the caller's), and only while the application is not shutting down.
    /// </summary>
    private void UpdateTrayMenu()
    {
        if (IsShuttingDown)
            return;

        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            ApplyTrayStatus();
            return;
        }

        try
        {
            _uiContext.Post(_ => ApplyTrayStatus(), null);
        }
        catch (InvalidOperationException) when (IsShuttingDown)
        {
            // The UI thread ended between the check and the call; there is no tray left to update.
        }
    }

    private void ApplyTrayStatus()
    {
        // Queued before shutdown began and delivered after it: the tray may already be gone.
        if (IsShuttingDown)
        {
            TrayUpdateIgnoredForTests?.Invoke();
            return;
        }

        ApplySavedLanguage();
        ApplyTrayLocalization();
        // The first-run window opens from the message loop, never inside the update that learned the state (which can
        // run in the constructor).
        if (!_firstStateHandled)
            _uiContext.Post(_ => HandleFirstState(), null);

        var status = _engine.GetStatus();
        _connectionMenuItem.Text = _localizer.GetTrayConnectionLabel(status);
        _trayIcon.Text = _localizer.GetTrayTooltip(
            status.TrayIconState,
            PrintBridgePaths.ProductDisplayName);

        var nextIcon = TrayIconFactory.Create(status.TrayIconState);
        var previousIcon = _trayIcon.Icon;
        _trayIcon.Icon = nextIcon;
        previousIcon?.Dispose();
    }

    private string GetUserErrorMessage(Exception ex)
    {
        if (ex is PrintBridgeConnectionException connectionEx)
            return _localizer.GetRuntimeIssueDetail(new(
                connectionEx.IssueCode,
                connectionEx.UserMessageKey,
                connectionEx.FormatArgs));

        if (ex is LocalizedApplicationException localized)
            return _localizer.GetString(localized.ResourceKey, localized.Args);

        // Never exception text (it can be technical or come from elsewhere).
        return _localizer["Shell.Op.Failed"];
    }

    /// <summary>
    /// Entry point for an incoming <c>wasla-printbridge://setup</c> URI (from this instance's startup
    /// args or forwarded from a second instance). Marshals to the UI thread and runs automatic setup.
    /// </summary>
    public void HandleSetupUri(string uri)
    {
        // A link forwarded while the application is exiting is dropped; it can be opened again after a restart.
        if (IsShuttingDown)
            return;

        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            _ = RunAutoSetupAsync(uri);
            return;
        }

        try
        {
            _uiContext.Post(_ => _ = RunAutoSetupAsync(uri), null);
        }
        catch (InvalidOperationException) when (IsShuttingDown)
        {
            // The UI thread ended between the check and the call.
        }
    }

    private async Task RunAutoSetupAsync(string uri)
    {
        // Checked again here: a link queued to the UI thread before Exit may arrive after it.
        if (IsShuttingDown || Interlocked.Exchange(ref _autoSetupRunning, 1) == 1)
            return;

        // The link is handled in the app window and its result shown there. Without a usable WebView2 Runtime the
        // runtime notice opens instead; the link is still applied and its result shown in a message box.
        var shell = TryShowShell(tab: null) ? _shellForm : null;
        if (shell is null)
            ShowRuntimeNotice();

        try
        {
            // The service decides whether the link is trusted, applies it, verifies a saved connection and resumes
            // printing (WAS-61). A move to another restaurant asks this app's user, here, through the confirmation.
            var outcome = await _engine.ApplySetupLinkAsync(uri, ConfirmConnectionTransferAsync, _lifetime.Token).ConfigureAwait(true);
            if (IsShuttingDown)
                return;

            ReportAutoSetupOutcome(shell, outcome);
        }
        catch (PrintBridgeEngineBusyException)
        {
            // Another setup link or a pending move is being applied; as before, this link is not queued.
        }
        catch (Exception ex)
        {
            if (_lifetime.IsCancellationRequested)
                return;

            // Only localized text is shown; anything else becomes the generic setup failure.
            var message = ex is LocalizedApplicationException or PrintBridgeConnectionException
                ? GetUserErrorMessage(ex)
                : _localizer["Auto.Failed"];
            ReportSetupResult(shell, ShellOperationOutcome.Failed, message, MessageBoxIcon.Warning);
        }
        finally
        {
            Interlocked.Exchange(ref _autoSetupRunning, 0);
        }
    }

    /// <summary>Updates the tray and shows the result; the service already verified a saved connection.</summary>
    private void ReportAutoSetupOutcome(PrintBridgeShellForm? shell, PrintBridgeAutoSetupOutcome outcome)
    {
        var result = DescribeAutoSetupOutcome(outcome);

        UpdateTrayMenu();
        if (_lifetime.IsCancellationRequested)
            return;

        ReportSetupResult(
            shell,
            result.Connected
                ? ShellOperationOutcome.Succeeded
                : outcome == PrintBridgeAutoSetupOutcome.Cancelled ? ShellOperationOutcome.Cancelled : ShellOperationOutcome.Failed,
            _localizer[result.MessageKey],
            result.Icon,
            outcome == PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing ? "printer" : null);
    }

    /// <summary>
    /// The native confirmation before a setup link moves Print Bridge to another restaurant (WAS-91). Shown on the UI
    /// thread, in the app window when it is open, otherwise on its own; exiting the app closes it as cancelled.
    /// </summary>
    private Task<bool> ConfirmConnectionTransferAsync(
        PrintBridgeConnectionTransferConsentRequest request,
        CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsShuttingDown)
        {
            completion.SetResult(false);
            return completion.Task;
        }

        try
        {
            _uiContext.Post(_ => _ = AskAsync(), null);
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(false);
        }

        return completion.Task;

        async Task AskAsync()
        {
            try
            {
                if (IsShuttingDown || ct.IsCancellationRequested)
                {
                    completion.TrySetResult(false);
                    return;
                }

                bool approved;
                if (_shellForm is { IsDisposed: false } shell)
                {
                    approved = await shell.ConfirmConnectionTransferAsync(request).ConfigureAwait(true);
                }
                else
                {
                    using var dialog = ShellConfirmDialog.ForConnectionTransfer(
                        request,
                        _localizer,
                        _cultureService.IsRightToLeft,
                        ShellWindowTheme.IsWindowsAppDarkModeEnabled());
                    ConnectionTransferDialogCreatedForTests?.Invoke(dialog);
                    using var close = _lifetime.Token.Register(() => _uiContext.Post(_ => dialog.Close(), null));
                    approved = dialog.ShowDialog(_runtimeNotice is { IsDisposed: false } notice ? notice : null) == DialogResult.OK;
                }

                completion.TrySetResult(approved && !IsShuttingDown);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The connection change confirmation could not be shown; nothing was changed.");
                completion.TrySetResult(false);
            }
        }
    }

    /// <summary>For tests: runs once for a connection-change confirmation shown without the app window, before it is shown.</summary>
    internal Action<ShellConfirmDialog>? ConnectionTransferDialogCreatedForTests { get; set; }

    /// <summary>How a setup-link outcome is shown, and whether a connection was saved.</summary>
    internal readonly record struct AutoSetupResultView(string MessageKey, MessageBoxIcon Icon, bool Saved, bool Connected);

    /// <summary>
    /// Maps a setup-link outcome. A link refused while a print job is still being completed changed nothing: it is
    /// reported as not applied, and the saved connection is neither reloaded nor re-verified.
    /// </summary>
    internal static AutoSetupResultView DescribeAutoSetupOutcome(PrintBridgeAutoSetupOutcome outcome) =>
        outcome switch
        {
            PrintBridgeAutoSetupOutcome.Connected =>
                new("Auto.Connected", MessageBoxIcon.Information, Saved: true, Connected: true),
            PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing =>
                new("Auto.ConnectedPrinterMissing", MessageBoxIcon.Information, Saved: true, Connected: true),
            PrintBridgeAutoSetupOutcome.SavedButUnverified =>
                new("Auto.SavedUnverified", MessageBoxIcon.Warning, Saved: true, Connected: false),
            PrintBridgeAutoSetupOutcome.InvalidOrExpired =>
                new("Auto.InvalidOrExpired", MessageBoxIcon.Warning, Saved: false, Connected: false),
            PrintBridgeAutoSetupOutcome.PrintingInProgress =>
                new("Auto.PrintingInProgress", MessageBoxIcon.Warning, Saved: false, Connected: false),
            PrintBridgeAutoSetupOutcome.Cancelled =>
                new("Auto.Cancelled", MessageBoxIcon.Information, Saved: false, Connected: false),
            PrintBridgeAutoSetupOutcome.CurrentConnectionUnverified =>
                new("Auto.CurrentConnectionUnverified", MessageBoxIcon.Warning, Saved: false, Connected: false),
            PrintBridgeAutoSetupOutcome.DeviceLimitReached =>
                new("Auto.DeviceLimitReached", MessageBoxIcon.Warning, Saved: false, Connected: false),
            PrintBridgeAutoSetupOutcome.InstallationConflict =>
                new("Auto.InstallationConflict", MessageBoxIcon.Warning, Saved: false, Connected: false),
            PrintBridgeAutoSetupOutcome.ChangePending =>
                new("Auto.ChangePending", MessageBoxIcon.Warning, Saved: false, Connected: false),
            PrintBridgeAutoSetupOutcome.DestinationDeviceDisabled =>
                new("Auto.DestinationDeviceDisabled", MessageBoxIcon.Warning, Saved: false, Connected: false),
            // An earlier approved move was saved first: connected (to that restaurant), the new link not applied.
            PrintBridgeAutoSetupOutcome.PreviousChangeCompleted =>
                new("Auto.PreviousChangeCompleted", MessageBoxIcon.Information, Saved: true, Connected: true),
            PrintBridgeAutoSetupOutcome.InvalidLink =>
                new("Auto.InvalidLink", MessageBoxIcon.Warning, Saved: false, Connected: false),
            _ => new("Auto.Failed", MessageBoxIcon.Warning, Saved: false, Connected: false)
        };

    /// <summary>
    /// Shows a setup-link result in the app window when it is open and still running, otherwise (no usable WebView2
    /// Runtime, or the window failed meanwhile) in a message box.
    /// </summary>
    private void ReportSetupResult(
        PrintBridgeShellForm? shell,
        ShellOperationOutcome outcome,
        string message,
        MessageBoxIcon icon,
        string? navigateTo = null)
    {
        if (shell is { IsDisposed: false } && ReferenceEquals(shell, _shellForm))
        {
            shell.ShowShell();
            shell.ShowConnectionResult(outcome, message, navigateTo);
            return;
        }

        if (SetupResultMessageForTests is { } record)
        {
            record(message);
            return;
        }

        MessageBox.Show(message, PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, icon);
    }

    /// <summary>For tests: the tray menu as the user sees it, and the windows it opens.</summary>
    internal ContextMenuStrip TrayMenuForTests => _trayMenu;

    internal PrintBridgeShellForm? ShellFormForTests => _shellForm;

    /// <summary>For tests: runs once for a new WebView2 window, before it is first shown (to keep it off-screen).</summary>
    internal Action<PrintBridgeShellForm>? ShellFormCreatedForTests { get; set; }

    internal WebView2RuntimeNotice? RuntimeNoticeForTests => _runtimeNotice;

    /// <summary>For tests: runs once for a new runtime notice, before it is first shown (to keep it off-screen).</summary>
    internal Action<WebView2RuntimeNotice>? RuntimeNoticeCreatedForTests { get; set; }

    /// <summary>For tests: receives a setup-link result instead of the message box shown when no app window is usable.</summary>
    internal Action<string>? SetupResultMessageForTests { get; set; }

    internal NotifyIcon TrayIconForTests => _trayIcon;

    internal Task ExitForTests() => ExitAsync();

    internal IPrintBridgeEngine EngineForTests => _engine;

    /// <summary>For tests: the shutdown steps in the order they ran.</summary>
    internal IReadOnlyList<string> ShutdownStepsForTests
    {
        get
        {
            lock (_shutdownSteps)
                return [.. _shutdownSteps];
        }
    }

    /// <summary>For tests: raised when a tray update queued before shutdown is delivered after it began.</summary>
    internal Action? TrayUpdateIgnoredForTests { get; set; }

    private bool IsShuttingDown => Volatile.Read(ref _shutdownRequested) != 0;

    /// <summary>
    /// Closes the app (tray "Close app"; WAS-61: printing continues in the service). The first request enters the
    /// shutting-down state at once, so no window,
    /// setup link, tray update or printer test starts from then on, and queues the one shutdown sequence on the UI
    /// thread. Queued rather than run inside the caller (for example the Exit item's own click), so nothing the sequence
    /// disposes is still in use further up the stack. Every later or concurrent request, from any thread, returns the
    /// same task.
    /// </summary>
    internal Task ExitAsync()
    {
        if (Interlocked.CompareExchange(ref _shutdownRequested, 1, 0) != 0)
            return _shutdownCompleted.Task;

        _logger.LogInformation("Print Bridge app close requested.");
        _uiContext.Post(_ => _ = RunShutdownAsync(), null);
        return _shutdownCompleted.Task;
    }

    private async Task RunShutdownAsync()
    {
        try
        {
            await ShutdownAsync().ConfigureAwait(true);
            _shutdownCompleted.TrySetResult();
        }
        catch (Exception ex)
        {
            // Not expected: the steps are ordered so that none meets a disposed component. The failure is logged and
            // reported to whoever waits for the exit; the application still exits (see ShutdownAsync).
            _logger.LogError(ex, "Print Bridge shutdown did not complete cleanly.");
            _shutdownCompleted.TrySetException(ex);
        }
    }

    /// <summary>
    /// The one shutdown sequence (WAS-59), on the UI thread. WAS-61: the engine is the service's and keeps printing; closing
    /// the app never stops it.
    /// <list type="number">
    /// <item>Nothing new starts: a move confirmation still open is closed as a refusal, a setup link or connection change
    /// not yet sent to the service is abandoned, the tray stops following the engine, and the icon and windows disappear at
    /// once. A command already sent finishes in the service. A tray update queued earlier finds the shutting-down state
    /// and does nothing.</item>
    /// <item>The app window (WebView2), the runtime notice and the tray icon are disposed; nothing raises events for them
    /// any more.</item>
    /// <item>The connection to the service is closed and the message loop ends, so the process exits.</item>
    /// </list>
    /// </summary>
    private async Task ShutdownAsync()
    {
        RecordShutdownStep("shutdown-started");
        try
        {
            _lifetime.Cancel();
            _engine.StatusChanged -= OnRuntimeStatusChanged;
            _engine.BackgroundSetupCompleted -= OnBackgroundSetupCompleted;
            _cultureService.CultureChanged -= OnCultureChanged;
            _trayIcon.Visible = false;
            _shellForm?.Hide();
            _runtimeNotice?.Hide();
            RecordShutdownStep("ui-hidden");

            DisposeShellWindow();
            DisposeRuntimeNotice();
            DisposeTrayIcon();

            _logger.LogInformation("Print Bridge app closed; printing continues in the Print Bridge service.");
            await _services.DisposeAsync().ConfigureAwait(true);
            RecordShutdownStep("services-disposed");
        }
        finally
        {
            // Always, so the process ends even if a step above failed.
            ExitThread();
            RecordShutdownStep("thread-exited");
        }
    }

    private void DisposeShellWindow()
    {
        if (_shellForm is not { } shell)
            return;

        _shellForm = null;
        shell.ShellUnavailable -= OnShellUnavailable;
        shell.Dispose();
        RecordShutdownStep("app-window-disposed");
    }

    private void DisposeRuntimeNotice()
    {
        if (_runtimeNotice is not { } notice)
            return;

        _runtimeNotice = null;
        notice.FormClosed -= OnRuntimeNoticeClosed;
        notice.Dispose();
        RecordShutdownStep("runtime-notice-disposed");
    }

    private void DisposeTrayIcon()
    {
        var icon = _trayIcon.Icon;
        _trayIcon.Dispose();
        icon?.Dispose();
        _trayMenu.Dispose();
        RecordShutdownStep("tray-icon-disposed");
    }

    private void RecordShutdownStep(string step)
    {
        lock (_shutdownSteps)
            _shutdownSteps.Add(step);
    }
}
