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

    private readonly ToolStripMenuItem _classicFallbackMenuItem;

    private readonly ToolStripSeparator _classicFallbackSeparator;

    private readonly IWebView2RuntimeProbe _webViewRuntimeProbe;

    private readonly IPrinterCatalog _printerCatalog = new WindowsPrinterCatalog();

    /// <summary>Cancelled on Exit, so a connection change still in progress is abandoned instead of half-applied.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    private PrintBridgeShellForm? _shellForm;

    private bool _shellFailedThisSession;

    private bool _shellFallbackNotified;

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



        _connectionMenuItem = new ToolStripMenuItem { Enabled = false };

        _openMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowPrimaryWindow());

        _printHistoryMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowPrintHistory());

        _printerTestMenuItem = new ToolStripMenuItem(string.Empty, null, OnPrinterTestClicked);

        _settingsMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowSettings());

        _exitMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ExitApplication());

        // Emergency fallback while the WebView2 app is the normal window; hidden when the classic window is the default.
        _classicFallbackMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowMainWindow());

        _classicFallbackSeparator = new ToolStripSeparator();



        _trayMenu = new ContextMenuStrip();

        _trayMenu.Items.Add(_connectionMenuItem);

        _trayMenu.Items.Add(new ToolStripSeparator());

        _trayMenu.Items.Add(_openMenuItem);

        _trayMenu.Items.Add(_printHistoryMenuItem);

        _trayMenu.Items.Add(_printerTestMenuItem);

        _trayMenu.Items.Add(_settingsMenuItem);

        _trayMenu.Items.Add(_classicFallbackSeparator);

        _trayMenu.Items.Add(_classicFallbackMenuItem);

        _trayMenu.Items.Add(new ToolStripSeparator());

        _trayMenu.Items.Add(_exitMenuItem);

        _trayMenu.Opening += (_, _) => UpdateClassicFallbackMenuItem();

        UpdateClassicFallbackMenuItem();



        _trayIcon = new NotifyIcon

        {

            Icon = TrayIconFactory.Create(TrayIconState.ConnectionLost),

            Text = PrintBridgePaths.ProductDisplayName,

            Visible = true,

            ContextMenuStrip = _trayMenu

        };

        _trayIcon.DoubleClick += (_, _) => ShowPrimaryWindow();



        _runtime.StatusChanged += (_, _) => UpdateTrayMenu();

        _cultureService.CultureChanged += (_, _) => UpdateTrayMenu();

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

        _classicFallbackMenuItem.Text = _localizer["Tray.OpenClassicFallback"];

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
    /// Every tray entry point goes through here. When <c>Ui.Shell</c> is <c>WebView2</c> and the runtime is usable,
    /// the one WebView2 window is shown (or brought forward) on <paramref name="tab"/>. Otherwise, or when the app
    /// fails while opening, the classic window opens on the matching tab, as before.
    /// </summary>
    private void ShowAppWindow(string? tab, Action? classicTab)
    {
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

        if (decision.FallbackReason is ShellFallbackReason.RuntimeUnavailable or ShellFallbackReason.UnrecognizedSetting)
        {
            _services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Wasla.PrintBridge.Shell")
                .LogWarning("WebView2 shell not used; opening the classic window. Reason={Reason}", decision.FallbackReason);
        }

        if (decision.FallbackReason == ShellFallbackReason.RuntimeUnavailable)
            NotifyShellFallback("Shell.Fallback.RuntimeMissing");

        return false;
    }

    private ShellDecision DecideShell() =>
        _shellFailedThisSession
            ? new ShellDecision(PrintBridgeShellMode.WinForms, ShellFallbackReason.RuntimeUnavailable)
            : ShellSelection.Decide(_services.GetRequiredService<PrintBridgeSettingsHolder>().Ui.Shell, _webViewRuntimeProbe);

    /// <summary>
    /// The tray's classic-window entry is an emergency fallback, offered only while the WebView2 app is the window
    /// "Open" shows. Without <c>Ui.Shell = WebView2</c> the runtime is not probed at all.
    /// </summary>
    private void UpdateClassicFallbackMenuItem()
    {
        var offered = DecideShell().Mode == PrintBridgeShellMode.WebView2;
        _classicFallbackMenuItem.Visible = offered;
        _classicFallbackSeparator.Visible = offered;
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
        _shellForm.ClassicWindowRequested += (_, _) => ShowMainWindow();
        _shellForm.ShellUnavailable += OnShellUnavailable;
        ShellFormCreatedForTests?.Invoke(_shellForm);
        return _shellForm;
    }

    private void OnShellUnavailable(object? sender, EventArgs e)
    {
        _shellFailedThisSession = true;
        if (sender is PrintBridgeShellForm failed)
        {
            failed.ShellUnavailable -= OnShellUnavailable;
            if (ReferenceEquals(_shellForm, failed))
                _shellForm = null;
            failed.BeginInvoke(failed.Dispose);
        }

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



    private void UpdateTrayMenu()

    {

        void Apply()

        {

            ApplyTrayLocalization();



            var status = _runtime.GetStatus();

            _connectionMenuItem.Text = _localizer.GetTrayConnectionLabel(status);

            _trayIcon.Text = _localizer.GetTrayTooltip(

                status.TrayIconState,

                PrintBridgePaths.ProductDisplayName);



            var nextIcon = TrayIconFactory.Create(status.TrayIconState);

            var previousIcon = _trayIcon.Icon;

            _trayIcon.Icon = nextIcon;

            if (previousIcon is not null)

                previousIcon.Dispose();

        }



        if (_mainForm.IsHandleCreated && _mainForm.InvokeRequired)

            _mainForm.BeginInvoke(Apply);

        else

            Apply();

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
        void Dispatch() => _ = RunAutoSetupAsync(uri);

        if (_mainForm.IsHandleCreated && _mainForm.InvokeRequired)
            _mainForm.BeginInvoke((Action)Dispatch);
        else
            Dispatch();
    }

    private async Task RunAutoSetupAsync(string uri)
    {
        if (Interlocked.Exchange(ref _autoSetupRunning, 1) == 1)
            return;

        // With the WebView2 app, a setup link is handled in its window and the result is shown there; the classic
        // window keeps its own flow and message boxes.
        var shell = TryShowShell(tab: null, classicTab: null) ? _shellForm : null;
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

            if (result.Saved)
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

    internal void ExitForTests() => ExitApplication();

    private void ExitApplication()

    {

        // First, so a connection change or setup link still running stops before it writes anything.
        _lifetime.Cancel();

        _trayIcon.Visible = false;

        _trayIcon.Icon?.Dispose();

        _trayIcon.Dispose();

        _mainForm.FormClosing -= OnMainFormClosing;

        _mainForm.Close();

        _shellForm?.Dispose();

        _runtime.Dispose();

        _services.Dispose();

        ExitThread();

    }

}


