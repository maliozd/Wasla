using Microsoft.Extensions.DependencyInjection;

using Wasla.PrintBridge.Configuration;

using Wasla.PrintBridge.Localization;

using Wasla.PrintBridge.Models;

using Wasla.PrintBridge.Services;



namespace Wasla.PrintBridge.UI;



public sealed class TrayApplicationContext : ApplicationContext

{

    private readonly ServiceProvider _services;

    private readonly PrintBridgeRuntime _runtime;

    private readonly PrintBridgeLocalizer _localizer;

    private readonly PrintBridgeCultureService _cultureService;

    private readonly NotifyIcon _trayIcon;

    private readonly MainForm _mainForm;

    private readonly ContextMenuStrip _trayMenu;

    private readonly ToolStripMenuItem _connectionMenuItem;

    private readonly ToolStripMenuItem _openMenuItem;

    private readonly ToolStripMenuItem _printHistoryMenuItem;

    private readonly ToolStripMenuItem _printerTestMenuItem;

    private readonly ToolStripMenuItem _settingsMenuItem;

    private readonly ToolStripMenuItem _exitMenuItem;



    public TrayApplicationContext(ServiceProvider services)

    {

        _services = services;

        _runtime = services.GetRequiredService<PrintBridgeRuntime>();

        _localizer = services.GetRequiredService<PrintBridgeLocalizer>();

        _cultureService = services.GetRequiredService<PrintBridgeCultureService>();



        _mainForm = new MainForm(services, _runtime);

        _mainForm.FormClosing += OnMainFormClosing;



        _connectionMenuItem = new ToolStripMenuItem { Enabled = false };

        _openMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowMainWindow());

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

        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();



        _runtime.StatusChanged += (_, _) => UpdateTrayMenu();

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

            return _localizer.GetString(connectionEx.UserMessageKey, connectionEx.FormatArgs);



        if (ex is LocalizedApplicationException localized)

            return _localizer.GetString(localized.ResourceKey, localized.Args);



        return ex.Message;

    }



    private void ExitApplication()

    {

        _trayIcon.Visible = false;

        _trayIcon.Icon?.Dispose();

        _trayIcon.Dispose();

        _mainForm.FormClosing -= OnMainFormClosing;

        _mainForm.Close();

        _runtime.Dispose();

        _services.Dispose();

        ExitThread();

    }

}


