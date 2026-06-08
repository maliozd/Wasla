using Microsoft.Extensions.DependencyInjection;
using OrderHub.PrintBridge.Configuration;
using OrderHub.PrintBridge.Localization;
using OrderHub.PrintBridge.Models;
using OrderHub.PrintBridge.Services;

namespace OrderHub.PrintBridge.UI;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ServiceProvider _services;
    private readonly PrintBridgeRuntime _runtime;
    private readonly PrintBridgeLocalizer _localizer;
    private readonly PrintBridgeCultureService _cultureService;
    private readonly NotifyIcon _trayIcon;
    private readonly MainForm _mainForm;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ToolStripMenuItem _openMenuItem;
    private readonly ToolStripMenuItem _recentJobsMenuItem;
    private readonly ToolStripMenuItem _logsMenuItem;
    private readonly ToolStripMenuItem _settingsMenuItem;
    private readonly ToolStripMenuItem _startStopMenuItem;
    private readonly ToolStripMenuItem _exitMenuItem;

    public TrayApplicationContext(ServiceProvider services)
    {
        _services = services;
        _runtime = services.GetRequiredService<PrintBridgeRuntime>();
        _localizer = services.GetRequiredService<PrintBridgeLocalizer>();
        _cultureService = services.GetRequiredService<PrintBridgeCultureService>();

        _mainForm = new MainForm(services, _runtime);
        _mainForm.FormClosing += OnMainFormClosing;

        _openMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowMainWindow());
        _recentJobsMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowRecentJobs());
        _logsMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowLogs());
        _settingsMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowSettings());
        _startStopMenuItem = new ToolStripMenuItem(string.Empty, null, OnStartStopClicked);
        _exitMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ExitApplication());

        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add(_openMenuItem);
        _trayMenu.Items.Add(_settingsMenuItem);
        _trayMenu.Items.Add(_recentJobsMenuItem);
        _trayMenu.Items.Add(_logsMenuItem);
        _trayMenu.Items.Add(_startStopMenuItem);
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
        _settingsMenuItem.Text = _localizer["Tray.Settings"];
        _recentJobsMenuItem.Text = _localizer["Tray.RecentJobs"];
        _logsMenuItem.Text = _localizer["Tray.Logs"];
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

    private void ShowRecentJobs()
    {
        ShowMainWindow();
        _mainForm.SelectStatusTab();
    }

    private void ShowLogs()
    {
        ShowMainWindow();
        _mainForm.SelectLogsTab();
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
            _mainForm.Hide();
            _mainForm.ShowInTaskbar = false;
        }
    }

    private void OnStartStopClicked(object? sender, EventArgs e)
    {
        if (_runtime.IsRunning)
        {
            _ = _runtime.StopAsync();
            return;
        }

        try
        {
            _runtime.Start();
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
            _startStopMenuItem.Text = status.IsRunning ? _localizer["Tray.Stop"] : _localizer["Tray.Start"];
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
