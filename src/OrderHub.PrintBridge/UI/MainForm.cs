using Microsoft.Extensions.DependencyInjection;
using OrderHub.PrintBridge.Configuration;
using OrderHub.PrintBridge.Models;
using OrderHub.PrintBridge.Options;
using OrderHub.PrintBridge.Printing;
using OrderHub.PrintBridge.Services;

namespace OrderHub.PrintBridge.UI;

public sealed class MainForm : Form
{
    private readonly ServiceProvider _services;
    private readonly PrintBridgeRuntime _runtime;
    private readonly PrintBridgeSettingsStore _settingsStore;
    private readonly PrintBridgeSettingsHolder _settingsHolder;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly TabControl _tabs;
    private readonly TabPage _statusTab;
    private readonly TabPage _settingsTab;

    private readonly Label _connectionStatusValue;
    private readonly Label _lastContactValue;
    private readonly Label _baseUrlValue;
    private readonly Label _printerValue;
    private readonly Label _bridgeValue;
    private readonly Label _printStatusValue;
    private readonly Label _lastPollValue;
    private readonly Label _lastErrorValue;
    private readonly DataGridView _recentJobsGrid;
    private readonly Button _btnTestConnection;
    private readonly Button _btnTestPrinter;
    private readonly Button _btnStartStop;
    private readonly Button _btnOpenLogs;

    private readonly TextBox _txtBaseUrl;
    private readonly TextBox _txtAgentToken;
    private readonly ComboBox _cmbPrinterName;
    private readonly Button _btnRefreshPrinters;
    private readonly TextBox _txtBridgeName;
    private readonly CheckBox _chkDryRun;
    private readonly NumericUpDown _numIdlePoll;
    private readonly NumericUpDown _numBusyPoll;
    private readonly NumericUpDown _numErrorPoll;
    private readonly Button _btnSaveSettings;

    public MainForm(ServiceProvider services, PrintBridgeRuntime runtime)
    {
        _services = services;
        _runtime = runtime;
        _settingsStore = services.GetRequiredService<PrintBridgeSettingsStore>();
        _settingsHolder = services.GetRequiredService<PrintBridgeSettingsHolder>();

        Text = PrintBridgePaths.ProductDisplayName;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(920, 620);
        Size = new Size(960, 680);
        Font = new Font("Segoe UI", 9F);

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _statusTab = new TabPage("Status");
        _settingsTab = new TabPage("Settings");
        _tabs.TabPages.Add(_statusTab);
        _tabs.TabPages.Add(_settingsTab);
        Controls.Add(_tabs);

        var statusLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12)
        };
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _statusTab.Controls.Add(statusLayout);

        var connectionGroup = CreateGroupBox("Connection");
        var connectionTable = CreateTwoColumnTable(5);
        _connectionStatusValue = AddStatusRow(connectionTable, "Status", 0);
        _lastContactValue = AddStatusRow(connectionTable, "Last successful contact", 1);
        _baseUrlValue = AddStatusRow(connectionTable, "Base URL", 2);
        _printerValue = AddStatusRow(connectionTable, "Printer", 3);
        _bridgeValue = AddStatusRow(connectionTable, "Bridge / machine", 4);
        connectionGroup.Controls.Add(connectionTable);
        statusLayout.Controls.Add(connectionGroup, 0, 0);

        var printGroup = CreateGroupBox("Print polling");
        var printTable = CreateTwoColumnTable(3);
        _printStatusValue = AddStatusRow(printTable, "Polling", 0);
        _lastPollValue = AddStatusRow(printTable, "Last poll", 1);
        _lastErrorValue = AddStatusRow(printTable, "Last error", 2);
        printGroup.Controls.Add(printTable);
        statusLayout.Controls.Add(printGroup, 0, 1);

        var jobsGroup = CreateGroupBox("Recent jobs (this session)");
        _recentJobsGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false
        };
        _recentJobsGrid.Columns.Add("JobId", "Job");
        _recentJobsGrid.Columns.Add("Order", "Order");
        _recentJobsGrid.Columns.Add("Status", "Status");
        _recentJobsGrid.Columns.Add("Created", "Created");
        _recentJobsGrid.Columns.Add("Printed", "Printed / attempt");
        _recentJobsGrid.Columns.Add("Error", "Error");
        jobsGroup.Controls.Add(_recentJobsGrid);
        statusLayout.Controls.Add(jobsGroup, 0, 2);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 8, 0, 0)
        };
        _btnTestConnection = new Button { Text = "Test connection", AutoSize = true };
        _btnTestConnection.Click += async (_, _) => await RunSafeAsync(TestConnectionAsync);
        buttonPanel.Controls.Add(_btnTestConnection);

        _btnTestPrinter = new Button { Text = "Test printer", AutoSize = true };
        _btnTestPrinter.Click += async (_, _) => await RunSafeAsync(TestPrinterAsync);
        buttonPanel.Controls.Add(_btnTestPrinter);

        _btnStartStop = new Button { Text = "Start polling", AutoSize = true };
        _btnStartStop.Click += (_, _) => TogglePolling();
        buttonPanel.Controls.Add(_btnStartStop);

        _btnOpenLogs = new Button { Text = "Open logs folder", AutoSize = true };
        _btnOpenLogs.Click += (_, _) => OpenLogsFolder();
        buttonPanel.Controls.Add(_btnOpenLogs);
        statusLayout.Controls.Add(buttonPanel, 0, 3);

        var settingsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 9,
            Padding = new Padding(12),
            AutoSize = true
        };
        settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _settingsTab.Controls.Add(settingsLayout);

        _txtBaseUrl = AddSettingsTextRow(settingsLayout, "Base URL", 0);
        _txtAgentToken = AddSettingsTextRow(settingsLayout, "Agent token", 1);
        _txtAgentToken.UseSystemPasswordChar = true;

        settingsLayout.Controls.Add(new Label { Text = "Printer name", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        var printerPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true
        };
        printerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        printerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _cmbPrinterName = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDown,
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        _btnRefreshPrinters = new Button { Text = "Refresh", AutoSize = true, Anchor = AnchorStyles.Left };
        _btnRefreshPrinters.Click += (_, _) => RefreshPrinterList();
        printerPanel.Controls.Add(_cmbPrinterName, 0, 0);
        printerPanel.Controls.Add(_btnRefreshPrinters, 1, 0);
        settingsLayout.Controls.Add(printerPanel, 1, 2);

        _txtBridgeName = AddSettingsTextRow(settingsLayout, "Bridge name", 3);

        settingsLayout.Controls.Add(new Label { Text = "Dry run", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        _chkDryRun = new CheckBox { Text = "Log receipts only; do not send to printer", AutoSize = true, Anchor = AnchorStyles.Left };
        settingsLayout.Controls.Add(_chkDryRun, 1, 4);

        _numIdlePoll = AddSettingsNumericRow(settingsLayout, "Idle poll (seconds)", 5, 1, 300, 5);
        _numBusyPoll = AddSettingsNumericRow(settingsLayout, "Busy poll (seconds)", 6, 1, 60, 1);
        _numErrorPoll = AddSettingsNumericRow(settingsLayout, "Error poll (seconds)", 7, 1, 300, 15);

        _btnSaveSettings = new Button { Text = "Save settings", AutoSize = true, Anchor = AnchorStyles.Left };
        _btnSaveSettings.Click += (_, _) => SaveSettings();
        settingsLayout.Controls.Add(new Panel(), 0, 8);
        settingsLayout.Controls.Add(_btnSaveSettings, 1, 8);

        var settingsHint = new Label
        {
            Text = $"Settings are stored at {PrintBridgePaths.ProgramDataConfigPath}",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Bottom,
            Padding = new Padding(12, 0, 12, 12)
        };
        _settingsTab.Controls.Add(settingsHint);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();

        _runtime.StatusChanged += (_, _) => BeginInvoke(RefreshStatus);
        LoadSettingsIntoForm();
        RefreshStatus();
    }

    public void SelectSettingsTab() => _tabs.SelectedTab = _settingsTab;

    private static GroupBox CreateGroupBox(string title) =>
        new()
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10)
        };

    private static TableLayoutPanel CreateTwoColumnTable(int rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < rows; i++)
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return table;
    }

    private static Label AddStatusRow(TableLayoutPanel table, string label, int row)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        var value = new Label { Text = "-", AutoSize = true, Anchor = AnchorStyles.Left };
        table.Controls.Add(value, 1, row);
        return value;
    }

    private static TextBox AddSettingsTextRow(TableLayoutPanel table, string label, int row)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        var textBox = new TextBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        table.Controls.Add(textBox, 1, row);
        return textBox;
    }

    private static NumericUpDown AddSettingsNumericRow(
        TableLayoutPanel table,
        string label,
        int row,
        decimal min,
        decimal max,
        decimal value)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        var numeric = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 100,
            Anchor = AnchorStyles.Left
        };
        table.Controls.Add(numeric, 1, row);
        return numeric;
    }

    private void LoadSettingsIntoForm()
    {
        var (hub, bridge) = _settingsHolder.Snapshot();
        _txtBaseUrl.Text = hub.BaseUrl;
        _txtAgentToken.Text = hub.AgentToken;
        RefreshPrinterList(bridge.PrinterName);
        _txtBridgeName.Text = string.IsNullOrWhiteSpace(bridge.BridgeName)
            ? Environment.MachineName
            : bridge.BridgeName;
        _chkDryRun.Checked = bridge.DryRun;
        _numIdlePoll.Value = Math.Clamp(bridge.IdlePollIntervalSeconds, (int)_numIdlePoll.Minimum, (int)_numIdlePoll.Maximum);
        _numBusyPoll.Value = Math.Clamp(bridge.BusyPollIntervalSeconds, (int)_numBusyPoll.Minimum, (int)_numBusyPoll.Maximum);
        _numErrorPoll.Value = Math.Clamp(bridge.ErrorPollIntervalSeconds, (int)_numErrorPoll.Minimum, (int)_numErrorPoll.Maximum);
    }

    private void RefreshPrinterList(string? selectedPrinter = null)
    {
        var current = selectedPrinter ?? _cmbPrinterName.Text.Trim();
        _cmbPrinterName.BeginUpdate();
        try
        {
            _cmbPrinterName.Items.Clear();
            foreach (var printer in RawPrinterHelper.ListInstalledPrinters())
                _cmbPrinterName.Items.Add(printer);

            if (!string.IsNullOrWhiteSpace(current))
            {
                var found = false;
                foreach (var item in _cmbPrinterName.Items)
                {
                    if (string.Equals(item?.ToString(), current, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    _cmbPrinterName.Items.Insert(0, current);

                _cmbPrinterName.Text = current;
            }
        }
        finally
        {
            _cmbPrinterName.EndUpdate();
        }
    }

    private void SaveSettings()
    {
        var orderHub = new OrderHubOptions
        {
            BaseUrl = _txtBaseUrl.Text.Trim(),
            AgentToken = _txtAgentToken.Text.Trim()
        };

        var bridge = new PrintBridgeOptions
        {
            PrinterMode = "WindowsPrinter",
            PrinterName = _cmbPrinterName.Text.Trim(),
            BridgeName = string.IsNullOrWhiteSpace(_txtBridgeName.Text.Trim())
                ? Environment.MachineName
                : _txtBridgeName.Text.Trim(),
            DryRun = _chkDryRun.Checked,
            IdlePollIntervalSeconds = (int)_numIdlePoll.Value,
            BusyPollIntervalSeconds = (int)_numBusyPoll.Value,
            ErrorPollIntervalSeconds = (int)_numErrorPoll.Value,
            MaxJobsPerPoll = _settingsHolder.Snapshot().Bridge.MaxJobsPerPoll
        };

        if (!PrintBridgeSettingsValidator.TryValidate(orderHub, bridge, out var error))
        {
            MessageBox.Show(error, PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settingsStore.Save(new PrintBridgeSettingsStore.AppSettingsDocument
        {
            OrderHub = orderHub,
            PrintBridge = bridge
        });
        _settingsHolder.Replace(orderHub, bridge);

        MessageBox.Show("Settings saved.", PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        RefreshStatus();
    }

    private void TogglePolling()
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

    private async Task TestConnectionAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _runtime.TestConnectionAsync(cts.Token).ConfigureAwait(true);
        MessageBox.Show("Connection successful.", PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task TestPrinterAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _runtime.TestPrinterAsync(cts.Token).ConfigureAwait(true);
        MessageBox.Show("Test print sent.", PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenLogsFolder()
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
        if (!Directory.Exists(PrintBridgePaths.ProgramDataLogDirectory))
        {
            MessageBox.Show("Logs folder does not exist yet.", PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = PrintBridgePaths.ProgramDataLogDirectory,
            UseShellExecute = true
        });
    }

    private async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshStatus()
    {
        var status = _runtime.GetStatus();
        _connectionStatusValue.Text = status.IsConnected ? "Connected" : "Not connected";
        _connectionStatusValue.ForeColor = status.IsConnected ? Color.DarkGreen : Color.DarkRed;
        _lastContactValue.Text = FormatUtc(status.LastSuccessfulContactUtc);
        _baseUrlValue.Text = string.IsNullOrWhiteSpace(status.BaseUrl) ? "(not set)" : status.BaseUrl;
        _printerValue.Text = string.IsNullOrWhiteSpace(status.PrinterName) ? "(not set)" : status.PrinterName;
        _bridgeValue.Text = status.BridgeName;
        _printStatusValue.Text = status.IsRunning
            ? status.DryRun ? "Running (dry run)" : "Running"
            : "Stopped";
        _printStatusValue.ForeColor = status.IsRunning ? Color.DarkGreen : Color.DarkRed;
        _lastPollValue.Text = FormatUtc(status.LastPollUtc);
        _lastErrorValue.Text = string.IsNullOrWhiteSpace(status.LastError) ? "-" : status.LastError;

        _btnStartStop.Text = status.IsRunning ? "Stop polling" : "Start polling";

        _recentJobsGrid.Rows.Clear();
        foreach (var job in status.RecentJobs)
        {
            _recentJobsGrid.Rows.Add(
                job.ShortJobId,
                string.IsNullOrWhiteSpace(job.OrderDisplay) ? job.OrderId.ToString("N")[..8] : job.OrderDisplay,
                job.Status.ToString(),
                FormatUtc(job.CreatedAtUtc),
                FormatUtc(job.PrintedAtUtc ?? job.LastAttemptAtUtc),
                job.ErrorMessage ?? string.Empty);
        }
    }

    private static string FormatUtc(DateTime? value) =>
        value.HasValue ? value.Value.ToLocalTime().ToString("g") : "-";

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosed(e);
    }
}
