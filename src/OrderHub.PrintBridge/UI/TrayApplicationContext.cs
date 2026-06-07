using Microsoft.Extensions.DependencyInjection;
using OrderHub.PrintBridge.Configuration;
using OrderHub.PrintBridge.Models;
using OrderHub.PrintBridge.Services;

namespace OrderHub.PrintBridge.UI;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ServiceProvider _services;
    private readonly PrintBridgeRuntime _runtime;
    private readonly NotifyIcon _trayIcon;
    private readonly MainForm _mainForm;
    private readonly ToolStripMenuItem _startStopMenuItem;

    public TrayApplicationContext(ServiceProvider services)
    {
        _services = services;
        _runtime = services.GetRequiredService<PrintBridgeRuntime>();

        _mainForm = new MainForm(services, _runtime);
        _mainForm.FormClosing += OnMainFormClosing;

        _startStopMenuItem = new ToolStripMenuItem("Başlat", null, OnStartStopClicked);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Aç", null, (_, _) => ShowMainWindow());
        menu.Items.Add(_startStopMenuItem);
        menu.Items.Add("Ayarlar", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(TrayIconState.ConnectionLost),
            Text = PrintBridgePaths.ProductDisplayName,
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        _runtime.StatusChanged += (_, _) => UpdateTrayMenu();
        UpdateTrayMenu();

        TryAutoStartPolling();
    }

    private void TryAutoStartPolling()
    {
        var holder = _services.GetRequiredService<PrintBridgeSettingsHolder>();
        var (hub, bridge) = holder.Snapshot();
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
            MessageBox.Show(ex.Message, PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateTrayMenu()
    {
        void Apply()
        {
            var status = _runtime.GetStatus();
            _startStopMenuItem.Text = status.IsRunning ? "Durdur" : "Başlat";

            _trayIcon.Text = status.TrayIconState switch
            {
                TrayIconState.Printing => $"{PrintBridgePaths.ProductDisplayName} (yazdırılıyor)",
                TrayIconState.Polling => $"{PrintBridgePaths.ProductDisplayName} (dinleniyor)",
                TrayIconState.Connected => $"{PrintBridgePaths.ProductDisplayName} (bağlı)",
                TrayIconState.ConnectionLost => $"{PrintBridgePaths.ProductDisplayName} (bağlantı yok)",
                _ => PrintBridgePaths.ProductDisplayName
            };

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
