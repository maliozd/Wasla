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

    private readonly IWebView2RuntimeProbe _webViewRuntimeProbe = new WebView2RuntimeProbe();

    private readonly IPrinterCatalog _printerCatalog = new WindowsPrinterCatalog();

    private PrintBridgeShellForm? _shellForm;

    private bool _shellFailedThisSession;

    private bool _shellFallbackNotified;



    public TrayApplicationContext(ServiceProvider services)

    {

        _services = services;

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

        _mainForm.Show();

        if (_mainForm.WindowState == FormWindowState.Minimized)

            _mainForm.WindowState = FormWindowState.Normal;

        _mainForm.ShowInTaskbar = true;

        _mainForm.Activate();

    }



    /// <summary>
    /// Tray "Open" and double-click. Opens the WebView2 status shell only when <c>Ui.Shell</c> is
    /// <c>WebView2</c> and the runtime is usable; otherwise, and for every other entry point (history,
    /// settings, automatic setup), the classic window opens as before.
    /// </summary>
    private void ShowPrimaryWindow()
    {
        var holder = _services.GetRequiredService<PrintBridgeSettingsHolder>();
        var decision = _shellFailedThisSession
            ? new ShellDecision(PrintBridgeShellMode.WinForms, ShellFallbackReason.RuntimeUnavailable)
            : ShellSelection.Decide(holder.Ui.Shell, _webViewRuntimeProbe);

        if (decision.Mode == PrintBridgeShellMode.WebView2)
        {
            GetOrCreateShellForm().ShowShell();
            return;
        }

        if (decision.FallbackReason is ShellFallbackReason.RuntimeUnavailable or ShellFallbackReason.UnrecognizedSetting)
        {
            _services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Wasla.PrintBridge.Shell")
                .LogWarning("WebView2 shell not used; opening the classic window. Reason={Reason}", decision.FallbackReason);
        }

        if (decision.FallbackReason == ShellFallbackReason.RuntimeUnavailable)
            NotifyShellFallback("Shell.Fallback.RuntimeMissing");

        ShowMainWindow();
    }

    private PrintBridgeShellForm GetOrCreateShellForm()
    {
        if (_shellForm is { IsDisposed: false })
            return _shellForm;

        var store = _services.GetRequiredService<PrintBridgeSettingsStore>();
        var holder = _services.GetRequiredService<PrintBridgeSettingsHolder>();
        var loggers = _services.GetRequiredService<ILoggerFactory>();
        _shellForm = new PrintBridgeShellForm(
            _runtime,
            holder,
            _printerCatalog,
            new ShellPrinterSettings(store, holder),
            _localizer,
            _cultureService,
            new PrintBridgeLanguageService(store, holder, _cultureService, loggers.CreateLogger<PrintBridgeLanguageService>()),
            _webViewRuntimeProbe.Probe().Version,
            loggers.CreateLogger("Wasla.PrintBridge.Shell"));
        _shellForm.ClassicWindowRequested += (_, _) => ShowMainWindow();
        _shellForm.ConnectionSetupRequested += (_, _) =>
        {
            // Reconnect uses the existing native setup: the token never passes through the WebView2 page.
            ShowMainWindow();
            _mainForm.FocusConnectionSettingsSection();
        };
        _shellForm.ShellUnavailable += OnShellUnavailable;
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
    }

    private void NotifyShellFallback(string messageKey)
    {
        if (_shellFallbackNotified)
            return;

        _shellFallbackNotified = true;
        _trayIcon.ShowBalloonTip(
            5000,
            _localizer["Shell.Fallback.Title"],
            _localizer[messageKey],
            ToolTipIcon.Info);
    }



    private void ShowPrintHistory()

    {

        ShowMainWindow();

        _mainForm.SelectPrintHistoryTab();

    }



    private void ShowSettings()

    {

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

        try
        {
            ShowMainWindow();

            if (!Wasla.PrintBridge.Setup.PrintBridgeProtocolUri.TryParseSetup(uri, out var request, out _))
            {
                MessageBox.Show(
                    _localizer["Auto.InvalidLink"],
                    PrintBridgePaths.ProductDisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var outcome = await _autoSetup.ApplyAsync(request!, cts.Token).ConfigureAwait(true);

            var (messageKey, icon) = outcome switch
            {
                Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.Connected =>
                    ("Auto.Connected", MessageBoxIcon.Information),
                Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing =>
                    ("Auto.ConnectedPrinterMissing", MessageBoxIcon.Information),
                Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.SavedButUnverified =>
                    ("Auto.SavedUnverified", MessageBoxIcon.Warning),
                Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.InvalidOrExpired =>
                    ("Auto.InvalidOrExpired", MessageBoxIcon.Warning),
                _ => ("Auto.Failed", MessageBoxIcon.Warning)
            };

            UpdateTrayMenu();
            _mainForm.RefreshAfterAutomaticSetup();

            if (outcome == Wasla.PrintBridge.Setup.PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing)
                _mainForm.FocusPrinterSettingsSection();

            MessageBox.Show(
                _localizer[messageKey],
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                icon);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                GetUserErrorMessage(ex),
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            Interlocked.Exchange(ref _autoSetupRunning, 0);
        }
    }

    private void ExitApplication()

    {

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


