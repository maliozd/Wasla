using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using Wasla.PrintBridge.Configuration;

using Wasla.PrintBridge.Localization;

using Wasla.PrintBridge.Models;

using Wasla.PrintBridge.Services;

using Wasla.PrintBridge.Setup;

using Wasla.PrintBridge.WebShell;



namespace Wasla.PrintBridge.UI;



public sealed class TrayApplicationContext : ApplicationContext

{

    private readonly ServiceProvider _services;

    private readonly PrintBridgeRuntime _runtime;

    private readonly PrintBridgeLocalizer _localizer;

    private readonly PrintBridgeCultureService _cultureService;

    private readonly NotifyIcon _trayIcon;

    private readonly MainForm _mainForm;

    private readonly PrintBridgeAutoSetupCoordinator _autoSetup;

    private int _autoSetupRunning;

    private readonly ContextMenuStrip _trayMenu;

    private readonly ToolStripMenuItem _connectionMenuItem;

    private readonly ToolStripMenuItem _openMenuItem;

    private readonly ToolStripMenuItem _printHistoryMenuItem;

    private readonly ToolStripMenuItem _printerTestMenuItem;

    private readonly ToolStripMenuItem _settingsMenuItem;

    private readonly ToolStripMenuItem _exitMenuItem;

    private readonly IWebView2RuntimeProbe _webViewRuntimeProbe;

    private readonly IPrinterCatalog _printerCatalog = new WindowsPrinterCatalog();

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

    private bool _shellFailedThisSession;

    private bool _shellFallbackNotified;

    private bool _ignoredShellSettingLogged;

    /// <summary>The classic tab to select if the WebView2 app fails while opening a requested tab.</summary>
    private Action? _classicTabForFallback;



    public TrayApplicationContext(ServiceProvider services)
        : this(services, new WebView2RuntimeProbe())
    {
    }

    /// <summary>For tests: a runtime probe that can report the WebView2 Runtime as missing.</summary>
    internal TrayApplicationContext(ServiceProvider services, IWebView2RuntimeProbe webViewRuntimeProbe)

    {

        _services = services;

        _webViewRuntimeProbe = webViewRuntimeProbe;

        _runtime = services.GetRequiredService<PrintBridgeRuntime>();

        _localizer = services.GetRequiredService<PrintBridgeLocalizer>();

        _cultureService = services.GetRequiredService<PrintBridgeCultureService>();

        _autoSetup = services.GetRequiredService<Wasla.PrintBridge.Setup.PrintBridgeAutoSetupCoordinator>();



        _mainForm = new MainForm(services, _runtime);

        _mainForm.FormClosing += OnMainFormClosing;

        // Creating the window installed the Windows Forms context on this thread, the UI thread.
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("The tray application must be created on the UI thread.");

        _uiThreadId = Environment.CurrentManagedThreadId;

        _logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Wasla.PrintBridge.Tray");



        _connectionMenuItem = new ToolStripMenuItem { Enabled = false };

        _openMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowPrimaryWindow());

        _printHistoryMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowPrintHistory());

        _printerTestMenuItem = new ToolStripMenuItem(string.Empty, null, OnPrinterTestClicked);

        _settingsMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowSettings());

        _exitMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => _ = ExitAsync());



        _trayMenu = new ContextMenuStrip();

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



        _runtime.StatusChanged += OnRuntimeStatusChanged;

        _cultureService.CultureChanged += OnCultureChanged;

        ApplyTrayLocalization();

        UpdateTrayMenu();



        TryAutoStartPolling();

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



    private void TryAutoStartPolling()

    {

        var holder = _services.GetRequiredService<PrintBridgeSettingsHolder>();

        var (hub, bridge, _) = holder.Snapshot();

        if (!PrintBridgeSettingsValidator.TryValidate(hub, bridge, out _))

            return;



        try

        {

            _runtime.Start();

        }

        catch

        {

        }

    }



    private void ShowMainWindow()

    {

        if (IsShuttingDown)
            return;

        // The WebView2 app may have changed settings while this window was hidden.
        if (!_mainForm.Visible)
            _mainForm.ReloadSettings();

        _mainForm.Show();

        if (_mainForm.WindowState == FormWindowState.Minimized)

            _mainForm.WindowState = FormWindowState.Normal;

        _mainForm.ShowInTaskbar = true;

        _mainForm.Activate();

    }



    /// <summary>Tray "Open" and double-click.</summary>
    private void ShowPrimaryWindow() => ShowAppWindow(tab: null, classicTab: null);

    /// <summary>
    /// Every tray entry point goes through here. By default the one WebView2 window is shown (or brought forward) on
    /// <paramref name="tab"/>. The classic window opens on the matching tab only for the explicit <c>WinForms</c>
    /// rollback, when the WebView2 Runtime is unusable, or once the app has failed this session.
    /// </summary>
    private void ShowAppWindow(string? tab, Action? classicTab)
    {
        if (IsShuttingDown)
            return;

        if (TryShowShell(tab, classicTab))
            return;

        ShowMainWindow();
        classicTab?.Invoke();
    }

    private bool TryShowShell(string? tab, Action? classicTab)
    {
        var decision = DecideShell();
        if (decision.Mode == PrintBridgeShellMode.WebView2)
        {
            _classicTabForFallback = classicTab;
            GetOrCreateShellForm().ShowShell(tab);
            return true;
        }

        if (decision.FallbackReason == ShellFallbackReason.RuntimeUnavailable)
        {
            _logger.LogWarning("WebView2 Runtime missing or too old; opening the classic window as the fallback.");
            NotifyShellFallback("Shell.Fallback.RuntimeMissing");
        }

        return false;
    }

    private ShellDecision DecideShell()
    {
        if (_shellFailedThisSession)
            return new ShellDecision(PrintBridgeShellMode.WinForms, ShellFallbackReason.RuntimeUnavailable);

        var decision = ShellSelection.Decide(_services.GetRequiredService<PrintBridgeSettingsHolder>().Ui.Shell, _webViewRuntimeProbe);
        // Once per session; the value itself is not logged.
        if (decision.IgnoredSetting && !_ignoredShellSettingLogged)
        {
            _ignoredShellSettingLogged = true;
            _logger.LogWarning("Unrecognized Ui.Shell value ignored; the WebView2 app is used. Use WebView2 or WinForms.");
        }

        return decision;
    }

    private PrintBridgeShellForm GetOrCreateShellForm()
    {
        if (_shellForm is { IsDisposed: false })
            return _shellForm;

        var store = _services.GetRequiredService<PrintBridgeSettingsStore>();
        var holder = _services.GetRequiredService<PrintBridgeSettingsHolder>();
        var loggers = _services.GetRequiredService<ILoggerFactory>();
        var logger = loggers.CreateLogger("Wasla.PrintBridge.Shell");
        _shellForm = new PrintBridgeShellForm(
            _runtime,
            holder,
            _printerCatalog,
            new ShellPrinterSettings(store, holder),
            new ShellOperationalSettings(store, holder),
            new ShellConnectionSetup(_runtime, holder, _localizer, logger, _lifetime.Token),
            _localizer,
            _cultureService,
            new PrintBridgeLanguageService(store, holder, _cultureService, loggers.CreateLogger<PrintBridgeLanguageService>()),
            _webViewRuntimeProbe.Probe().Version,
            logger);
        // The app no longer offers the classic window; it opens only for the WinForms rollback or when the app fails.
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

        // The engine and the tray are not affected: listening and printing go on while the notice and the classic
        // window are shown.
        _logger.LogWarning("The WebView2 app failed; opening the classic window as the fallback for this session.");
        NotifyShellFallback("Shell.Fallback.StartFailed");
        ShowMainWindow();
        _classicTabForFallback?.Invoke();
        _classicTabForFallback = null;
    }

    private void NotifyShellFallback(string messageKey)
    {
        if (_shellFallbackNotified)
            return;

        _shellFallbackNotified = true;
        FallbackNoticeForTests = messageKey;
        // A balloon needs a visible tray icon; tests hide it so nothing appears on the desktop.
        if (!_trayIcon.Visible)
            return;

        _trayIcon.ShowBalloonTip(
            5000,
            _localizer["Shell.Fallback.Title"],
            _localizer[messageKey],
            ToolTipIcon.Info);
    }



    private void ShowPrintHistory() => ShowAppWindow("history", _mainForm.SelectPrintHistoryTab);

    private void ShowSettings() => ShowAppWindow("settings", _mainForm.SelectSettingsTab);

    /// <summary>
    /// First run, or no usable device token (for example after the token was reset or rejected): opens the connection
    /// setup instead of leaving the user to find the tray icon. The WebView2 app opens on its Settings tab and starts the
    /// connection dialog; with the classic window (WinForms rollback or fallback) that window opens on Settings. With a
    /// valid connection nothing opens and the start stays in the tray. Called once after startup, unless a setup link
    /// was passed on the command line, which then does the setup.
    /// </summary>
    public void ShowSetupIfNotConnected()
    {
        if (IsShuttingDown)
            return;

        var connection = _services.GetRequiredService<PrintBridgeSettingsHolder>().OrderHub;
        if (PrintBridgeSettingsValidator.TryValidateConnectionSettings(connection, out _))
            return;

        _logger.LogInformation("No usable connection is configured; opening the connection setup.");
        if (TryShowShell("settings", _mainForm.SelectSettingsTab))
        {
            _shellForm?.StartConnectionSetup();
            return;
        }

        ShowMainWindow();
        _mainForm.SelectSettingsTab();
    }



    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)

    {

        if (e.CloseReason == CloseReason.UserClosing)

        {

            e.Cancel = true;

            _mainForm.PersistWindowLayout();

            _mainForm.Hide();

            _mainForm.ShowInTaskbar = false;

        }

    }



    private async void OnPrinterTestClicked(object? sender, EventArgs e)

    {

        if (IsShuttingDown)
            return;

        try

        {

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await _runtime.TestPrinterAsync(cts.Token).ConfigureAwait(true);

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

        ApplyTrayLocalization();

        var status = _runtime.GetStatus();
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



        return ex.Message;

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

        void Dispatch() => _ = RunAutoSetupAsync(uri);

        if (_mainForm.IsHandleCreated && _mainForm.InvokeRequired)
            _mainForm.BeginInvoke((Action)Dispatch);
        else
            Dispatch();
    }

    private async Task RunAutoSetupAsync(string uri)
    {
        // Checked again here: a link queued to the UI thread before Exit may arrive after it.
        if (IsShuttingDown || Interlocked.Exchange(ref _autoSetupRunning, 1) == 1)
            return;

        // With the WebView2 app, a setup link is handled in its window and the result is shown there; the classic
        // window keeps its own flow and message boxes.
        var shell = TryShowShell(tab: null, classicTab: null) ? _shellForm : null;
        // The link takes over from an open connection dialog (for example the one opened on first run), so the user
        // is not left with a stale "first setup" dialog that reports "not changed" or replaces what the link set up.
        shell?.CloseConnectionDialogForSetupLink();
        try
        {
            if (shell is null)
                ShowMainWindow();

            if (!Wasla.PrintBridge.Setup.PrintBridgeProtocolUri.TryParseSetup(uri, out var request, out _))
            {
                ReportSetupResult(shell, ShellOperationOutcome.Failed, _localizer["Auto.InvalidLink"], MessageBoxIcon.Warning);
                return;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            var outcome = await _autoSetup.ApplyAsync(request!, cts.Token).ConfigureAwait(true);
            if (IsShuttingDown)
                return;

            var result = DescribeAutoSetupOutcome(outcome);

            UpdateTrayMenu();

            if (shell is null)
            {
                // A refused link changed nothing, so the open window keeps what is on screen.
                if (outcome != Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.PrintingInProgress)
                    _mainForm.RefreshAfterAutomaticSetup();

                if (outcome == Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing)
                    _mainForm.FocusPrinterSettingsSection();

                MessageBox.Show(
                    _localizer[result.MessageKey],
                    PrintBridgePaths.ProductDisplayName,
                    MessageBoxButtons.OK,
                    result.Icon);
                return;
            }

            if (result.Saved && !IsShuttingDown)
            {
                // Same recovery as the classic window: verify the saved connection and resume listening.
                using var verify = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                verify.CancelAfter(TimeSpan.FromSeconds(30));
                await _runtime.VerifyAndResumeAsync(verify.Token).ConfigureAwait(true);
            }

            if (_lifetime.IsCancellationRequested)
                return;

            ReportSetupResult(
                shell,
                result.Connected ? ShellOperationOutcome.Succeeded : ShellOperationOutcome.Failed,
                _localizer[result.MessageKey],
                result.Icon,
                outcome == Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing ? "printer" : null);
        }
        catch (Exception ex)
        {
            if (_lifetime.IsCancellationRequested)
                return;

            // The page shows only localized text; anything else becomes the generic setup failure.
            var message = shell is null || ex is LocalizedApplicationException or PrintBridgeConnectionException
                ? GetUserErrorMessage(ex)
                : _localizer["Auto.Failed"];
            ReportSetupResult(shell, ShellOperationOutcome.Failed, message, MessageBoxIcon.Warning);
        }
        finally
        {
            Interlocked.Exchange(ref _autoSetupRunning, 0);
        }
    }

    /// <summary>How a setup-link outcome is shown, and whether a connection was saved.</summary>
    internal readonly record struct AutoSetupResultView(string MessageKey, MessageBoxIcon Icon, bool Saved, bool Connected);

    /// <summary>
    /// Maps a setup-link outcome for both windows. A link refused while a print job is still being completed changed
    /// nothing: it is reported as not applied, and the saved connection is neither reloaded nor re-verified.
    /// </summary>
    internal static AutoSetupResultView DescribeAutoSetupOutcome(Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome outcome) =>
        outcome switch
        {
            Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.Connected =>
                new("Auto.Connected", MessageBoxIcon.Information, Saved: true, Connected: true),
            Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing =>
                new("Auto.ConnectedPrinterMissing", MessageBoxIcon.Information, Saved: true, Connected: true),
            Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.SavedButUnverified =>
                new("Auto.SavedUnverified", MessageBoxIcon.Warning, Saved: true, Connected: false),
            Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.InvalidOrExpired =>
                new("Auto.InvalidOrExpired", MessageBoxIcon.Warning, Saved: false, Connected: false),
            Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.PrintingInProgress =>
                new("Auto.PrintingInProgress", MessageBoxIcon.Warning, Saved: false, Connected: false),
            _ => new("Auto.Failed", MessageBoxIcon.Warning, Saved: false, Connected: false)
        };

    /// <summary>
    /// Shows a setup-link result in the WebView2 app when it is the window in use (and still running), otherwise in a
    /// message box as before.
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

        MessageBox.Show(message, PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, icon);
    }

    /// <summary>For tests: the tray menu as the user sees it, and the windows it opens.</summary>
    internal ContextMenuStrip TrayMenuForTests => _trayMenu;

    internal PrintBridgeShellForm? ShellFormForTests => _shellForm;

    /// <summary>For tests: runs once for a new WebView2 window, before it is first shown (to keep it off-screen).</summary>
    internal Action<PrintBridgeShellForm>? ShellFormCreatedForTests { get; set; }

    internal MainForm ClassicWindowForTests => _mainForm;

    internal NotifyIcon TrayIconForTests => _trayIcon;

    /// <summary>For tests: the resource key of the fallback notice, once one was raised this session.</summary>
    internal string? FallbackNoticeForTests { get; private set; }

    internal Task ExitForTests() => ExitAsync();

    internal PrintBridgeRuntime RuntimeForTests => _runtime;

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
    /// Exits the application (tray Exit). The first request enters the shutting-down state at once, so no window,
    /// setup link, tray update or printer test starts from then on, and queues the one shutdown sequence on the UI
    /// thread. Queued rather than run inside the caller (for example the Exit item's own click), so nothing the sequence
    /// disposes is still in use further up the stack. Every later or concurrent request, from any thread, returns the
    /// same task.
    /// </summary>
    internal Task ExitAsync()
    {
        if (Interlocked.CompareExchange(ref _shutdownRequested, 1, 0) != 0)
            return _shutdownCompleted.Task;

        _logger.LogInformation("Print Bridge exit requested.");
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
    /// The one shutdown sequence (WAS-59), on the UI thread. Engine first, then what shows the engine, then the services:
    /// <list type="number">
    /// <item>Nothing new starts: a connection change or setup link still running is abandoned before it writes anything,
    /// the tray and the classic window stop following the engine, and the icon and windows disappear at once. A tray
    /// update queued earlier finds the shutting-down state and does nothing.</item>
    /// <item>The engine stops, awaited so the UI thread is never blocked. A claimed job is not cancelled: Stop waits for it
    /// up to its limit (<see cref="PrintBridgeRuntime.StopAsync"/>, WAS-56); a job that takes longer finishes in the
    /// background only while the process still runs.</item>
    /// <item>The app window (WebView2), the classic window and the tray icon are disposed; nothing raises events for them
    /// any more.</item>
    /// <item>The services are disposed and the message loop ends, so the process exits.</item>
    /// </list>
    /// </summary>
    private async Task ShutdownAsync()
    {
        RecordShutdownStep("shutdown-started");
        try
        {
            _lifetime.Cancel();
            _runtime.StatusChanged -= OnRuntimeStatusChanged;
            _cultureService.CultureChanged -= OnCultureChanged;
            _mainForm.DetachFromEngine();
            _trayIcon.Visible = false;
            _shellForm?.Hide();
            _mainForm.Hide();

            await _runtime.StopAsync().ConfigureAwait(true);
            RecordShutdownStep("runtime-stopped");

            DisposeShellWindow();
            DisposeClassicWindow();
            DisposeTrayIcon();

            // An operation that was already running when Exit began could have started listening again meanwhile; with
            // every window gone nothing can any more. Normally this returns at once.
            await _runtime.StopAsync().ConfigureAwait(true);

            _logger.LogInformation("Print Bridge shutdown complete; the application exits.");
            _services.Dispose();
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

    private void DisposeClassicWindow()
    {
        _mainForm.FormClosing -= OnMainFormClosing;
        // Closing saves the window layout when the window was created, as before.
        _mainForm.Close();
        _mainForm.Dispose();
        RecordShutdownStep("classic-window-disposed");
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


