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
    private readonly UiLogBuffer _uiLogBuffer;
    private readonly System.Windows.Forms.Timer _dashboardTimer;
    private readonly System.Windows.Forms.Timer _jobsRefreshTimer;

    private TabControl _tabs = null!;
    private TabPage _statusTab = null!;
    private TabPage _logsTab = null!;
    private TabPage _settingsTab = null!;

    private Label _headerBadge = null!;
    private Label _deviceStatusValue = null!;
    private Label _lastPollMetricValue = null!;
    private Label _jobsTodayValue = null!;
    private Label _failedTodayValue = null!;
    private DataGridView _recentJobsGrid = null!;
    private Label _recentJobsEmptyLabel = null!;
    private IReadOnlyList<LocalPrintJobRecord> _jobsForGrid = Array.Empty<LocalPrintJobRecord>();
    private TextBox _txtLogs = null!;
    private Button _btnTestConnection = null!;
    private Button _btnTestPrinter = null!;
    private Button _btnStartStop = null!;
    private Button _btnOpenLogs = null!;

    private TextBox _txtBaseUrl = null!;
    private TextBox _txtAgentToken = null!;
    private Button _btnToggleToken = null!;
    private ComboBox _cmbPrinterName = null!;
    private Button _btnRefreshPrinters = null!;
    private TextBox _txtBridgeName = null!;
    private CheckBox _chkDryRun = null!;
    private NumericUpDown _numIdlePoll = null!;
    private NumericUpDown _numBusyPoll = null!;
    private NumericUpDown _numErrorPoll = null!;
    private Button _btnSaveSettings = null!;

    public MainForm(ServiceProvider services, PrintBridgeRuntime runtime)
    {
        _services = services;
        _runtime = runtime;
        _settingsStore = services.GetRequiredService<PrintBridgeSettingsStore>();
        _settingsHolder = services.GetRequiredService<PrintBridgeSettingsHolder>();
        _uiLogBuffer = services.GetRequiredService<UiLogBuffer>();

        Text = PrintBridgePaths.ProductDisplayName;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 720);
        Size = new Size(1040, 780);
        Font = new Font("Segoe UI", 9F);
        BackColor = PrintBridgeUiTheme.PageBackground;

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9.5F),
            Padding = new Point(8, 6)
        };
        _statusTab = new TabPage("Durum") { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(12) };
        _logsTab = new TabPage("Loglar") { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(12) };
        _settingsTab = new TabPage("Ayarlar") { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(12) };
        _tabs.TabPages.Add(_statusTab);
        _tabs.TabPages.Add(_logsTab);
        _tabs.TabPages.Add(_settingsTab);
        Controls.Add(_tabs);

        BuildStatusTab();
        BuildLogsTab();
        BuildSettingsTab();

        _dashboardTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _dashboardTimer.Tick += (_, _) => RefreshDashboard();
        _dashboardTimer.Start();

        _jobsRefreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _jobsRefreshTimer.Tick += (_, _) => RefreshRecentJobsFromRuntime();
        _jobsRefreshTimer.Start();

        _runtime.StatusChanged += OnRuntimeStatusChanged;
        _uiLogBuffer.Changed += (_, _) => QueueRefreshLogs();
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab == _logsTab)
                RefreshLogs();
        };
        LoadSettingsIntoForm();

        if (!IsHandleCreated)
            CreateHandle();

        RefreshDashboard();
        RefreshRecentJobsFromRuntime();
        RefreshLogs();
    }

    private void BuildStatusTab()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _statusTab.Controls.Add(root);

        var header = new Panel { Dock = DockStyle.Fill, Height = 72, Margin = new Padding(0, 0, 0, 10) };
        var title = new Label
        {
            Text = "OrderHub Print Bridge",
            Font = PrintBridgeUiTheme.TitleFont,
            ForeColor = PrintBridgeUiTheme.TextTitle,
            AutoSize = true,
            Location = new Point(0, 0)
        };
        var subtitle = new Label
        {
            Text = "Fiş yazdırma köprüsü",
            Font = PrintBridgeUiTheme.SubtitleFont,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            AutoSize = true,
            Location = new Point(0, 30)
        };
        _headerBadge = new Label
        {
            Text = "Durduruldu",
            Font = PrintBridgeUiTheme.BadgeFont,
            ForeColor = Color.White,
            BackColor = PrintBridgeUiTheme.Inactive,
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            Location = new Point(0, 52)
        };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(_headerBadge);
        root.Controls.Add(header, 0, 0);

        var cardsRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        };
        cardsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cardsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cardsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cardsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        var deviceStatusCard = PrintBridgeUiTheme.CreateMetricCard("Device Status", out _deviceStatusValue);
        cardsRow.Controls.Add(deviceStatusCard, 0, 0);

        var lastPollCard = PrintBridgeUiTheme.CreateMetricCard("Last Poll", out _lastPollMetricValue);
        cardsRow.Controls.Add(lastPollCard, 1, 0);

        var jobsTodayCard = PrintBridgeUiTheme.CreateMetricCard("Jobs Today", out _jobsTodayValue);
        cardsRow.Controls.Add(jobsTodayCard, 2, 0);

        var failedTodayCard = PrintBridgeUiTheme.CreateMetricCard("Failed Today", out _failedTodayValue);
        cardsRow.Controls.Add(failedTodayCard, 3, 0);
        root.Controls.Add(cardsRow, 0, 1);

        var jobsGroup = new GroupBox
        {
            Text = "  Son işler (bu oturum)  ",
            Dock = DockStyle.Fill,
            Font = PrintBridgeUiTheme.SectionFont,
            ForeColor = PrintBridgeUiTheme.TextTitle,
            Padding = new Padding(10, 20, 10, 10),
            Margin = new Padding(0, 0, 0, 10)
        };
        var jobsPanel = new Panel { Dock = DockStyle.Fill, MinimumSize = new Size(0, 240) };
        _recentJobsGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false
        };
        PrintBridgeUiTheme.StyleGrid(_recentJobsGrid);
        _recentJobsGrid.Columns.Add("Time", "Time");
        _recentJobsGrid.Columns.Add("Order", "Order");
        _recentJobsGrid.Columns.Add("Type", "Type");
        _recentJobsGrid.Columns.Add("Printer", "Printer");
        _recentJobsGrid.Columns.Add("Status", "Status");
        _recentJobsGrid.Columns["Time"]!.FillWeight = 16;
        _recentJobsGrid.Columns["Order"]!.FillWeight = 28;
        _recentJobsGrid.Columns["Type"]!.FillWeight = 14;
        _recentJobsGrid.Columns["Printer"]!.FillWeight = 22;
        _recentJobsGrid.Columns["Status"]!.FillWeight = 20;
        _recentJobsGrid.CellFormatting += OnRecentJobsCellFormatting;
        _recentJobsGrid.CellToolTipTextNeeded += OnRecentJobsCellToolTipTextNeeded;
        _recentJobsGrid.ShowCellToolTips = true;
        _recentJobsEmptyLabel = new Label
        {
            Text = "Bu oturumda henüz yazdırma işi işlenmedi.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Font = new Font("Segoe UI", 10F)
        };
        jobsPanel.Controls.Add(_recentJobsGrid);
        jobsPanel.Controls.Add(_recentJobsEmptyLabel);
        jobsGroup.Controls.Add(jobsPanel);
        root.Controls.Add(jobsGroup, 0, 2);

        var actionBar = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        };
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        _btnStartStop = PrintBridgeUiTheme.CreateActionButton("Dinlemeyi başlat", primary: true);
        _btnStartStop.Click += (_, _) => TogglePolling();
        buttonPanel.Controls.Add(_btnStartStop);

        _btnTestConnection = PrintBridgeUiTheme.CreateActionButton("Bağlantıyı test et");
        _btnTestConnection.Click += async (_, _) => await RunSafeAsync(TestConnectionAsync);
        buttonPanel.Controls.Add(_btnTestConnection);

        _btnTestPrinter = PrintBridgeUiTheme.CreateActionButton("Yazıcıyı test et");
        _btnTestPrinter.Click += async (_, _) => await RunSafeAsync(TestPrinterAsync);
        buttonPanel.Controls.Add(_btnTestPrinter);

        _btnOpenLogs = PrintBridgeUiTheme.CreateActionButton("Log klasörünü aç");
        _btnOpenLogs.Click += (_, _) => OpenLogsFolder();
        buttonPanel.Controls.Add(_btnOpenLogs);

        actionBar.Controls.Add(buttonPanel);
        root.Controls.Add(actionBar, 0, 3);
    }

    private void BuildLogsTab()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8)
        };

        var btnClearLogs = PrintBridgeUiTheme.CreateActionButton("Clear Logs");
        btnClearLogs.Click += (_, _) =>
        {
            _uiLogBuffer.Clear();
            RefreshLogs();
        };
        toolbar.Controls.Add(btnClearLogs);

        var btnCopyLogs = PrintBridgeUiTheme.CreateActionButton("Copy Logs");
        btnCopyLogs.Click += (_, _) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_txtLogs.Text))
                    Clipboard.SetText(_txtLogs.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Loglar kopyalanamadı: {ex.Message}", PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        toolbar.Controls.Add(btnCopyLogs);

        var btnOpenLogsFolder = PrintBridgeUiTheme.CreateActionButton("Log klasörünü aç");
        btnOpenLogsFolder.Click += (_, _) => OpenLogsFolder();
        toolbar.Controls.Add(btnOpenLogsFolder);

        _txtLogs = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9F),
            BackColor = Color.FromArgb(248, 249, 250),
            BorderStyle = BorderStyle.FixedSingle
        };

        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(_txtLogs, 0, 1);
        _logsTab.Controls.Add(root);
    }

    private void BuildSettingsTab()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var settingsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            Padding = new Padding(4),
            Width = 900
        };
        settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _txtBaseUrl = AddSettingsTextRow(settingsLayout, "Sunucu adresi", 0);
        AddTokenRow(settingsLayout, 1);
        AddPrinterRow(settingsLayout, 2);
        _txtBridgeName = AddSettingsTextRow(settingsLayout, "Bilgisayar adı", 3);

        var advancedGroup = new GroupBox
        {
            Text = "  Gelişmiş  ",
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = PrintBridgeUiTheme.SectionFont,
            ForeColor = PrintBridgeUiTheme.TextTitle,
            Padding = new Padding(12, 18, 12, 12),
            Margin = new Padding(0, 8, 0, 8)
        };
        var advancedLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true
        };
        advancedLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        advancedLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        advancedLayout.Controls.Add(new Label
        {
            Text = "Test modu",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        }, 0, 0);
        _chkDryRun = new CheckBox
        {
            Text = "Fişi yazıcıya gönderme, sadece logla",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        advancedLayout.Controls.Add(_chkDryRun, 1, 0);

        _numIdlePoll = AddSettingsNumericRow(advancedLayout, "Boşta bekleme (sn)", 1, 1, 300, 5);
        _numBusyPoll = AddSettingsNumericRow(advancedLayout, "Yoğunken bekleme (sn)", 2, 1, 60, 1);
        _numErrorPoll = AddSettingsNumericRow(advancedLayout, "Hata sonrası bekleme (sn)", 3, 1, 300, 15);
        advancedGroup.Controls.Add(advancedLayout);

        settingsLayout.Controls.Add(advancedGroup, 0, 4);
        settingsLayout.SetColumnSpan(advancedGroup, 2);

        var savePanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 12, 0, 0)
        };
        _btnSaveSettings = PrintBridgeUiTheme.CreateActionButton("Ayarları kaydet", primary: true);
        _btnSaveSettings.Click += (_, _) => SaveSettings();
        savePanel.Controls.Add(_btnSaveSettings);
        settingsLayout.Controls.Add(savePanel, 0, 5);
        settingsLayout.SetColumnSpan(savePanel, 2);

        scroll.Controls.Add(settingsLayout);

        var settingsHint = new Label
        {
            Text = $"Ayarlar şuraya kaydedilir: {PrintBridgePaths.ProgramDataConfigPath}",
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Dock = DockStyle.Bottom,
            Padding = new Padding(4, 8, 4, 4)
        };

        var settingsHost = new Panel { Dock = DockStyle.Fill };
        settingsHost.Controls.Add(scroll);
        settingsHost.Controls.Add(settingsHint);
        _settingsTab.Controls.Add(settingsHost);
    }

    private void AddTokenRow(TableLayoutPanel table, int row)
    {
        table.Controls.Add(new Label
        {
            Text = "Erişim anahtarı",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        }, 0, row);

        var tokenPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true
        };
        tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _txtAgentToken = new TextBox
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        _btnToggleToken = new Button
        {
            Text = "Göster",
            AutoSize = true,
            MinimumSize = new Size(72, 28),
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(8, 0, 0, 0)
        };
        _btnToggleToken.Click += (_, _) =>
        {
            _txtAgentToken.UseSystemPasswordChar = !_txtAgentToken.UseSystemPasswordChar;
            _btnToggleToken.Text = _txtAgentToken.UseSystemPasswordChar ? "Göster" : "Gizle";
        };
        tokenPanel.Controls.Add(_txtAgentToken, 0, 0);
        tokenPanel.Controls.Add(_btnToggleToken, 1, 0);
        table.Controls.Add(tokenPanel, 1, row);
    }

    private void AddPrinterRow(TableLayoutPanel table, int row)
    {
        table.Controls.Add(new Label
        {
            Text = "Yazıcı adı",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        }, 0, row);

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
        _btnRefreshPrinters = new Button
        {
            Text = "Yenile",
            AutoSize = true,
            MinimumSize = new Size(72, 28),
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(8, 0, 0, 0)
        };
        _btnRefreshPrinters.Click += (_, _) => RefreshPrinterList();
        printerPanel.Controls.Add(_cmbPrinterName, 0, 0);
        printerPanel.Controls.Add(_btnRefreshPrinters, 1, 0);
        table.Controls.Add(printerPanel, 1, row);
    }

    private void OnRuntimeStatusChanged(object? sender, EventArgs e)
    {
        QueueRefreshDashboard();
        QueueRefreshRecentJobs();
    }

    private void QueueRefreshDashboard() => QueueUiAction(RefreshDashboard);

    private void QueueRefreshRecentJobs() => QueueUiAction(RefreshRecentJobsFromRuntime);

    private void QueueRefreshLogs() => QueueUiAction(RefreshLogs);

    private void QueueUiAction(Action action)
    {
        if (IsDisposed)
            return;

        if (!IsHandleCreated)
            CreateHandle();

        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    public void SelectSettingsTab() => _tabs.SelectedTab = _settingsTab;

    private static TextBox AddSettingsTextRow(TableLayoutPanel table, string label, int row)
    {
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        }, 0, row);
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
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        }, 0, row);
        var numeric = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 110,
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

        try
        {
            _settingsStore.Save(new PrintBridgeSettingsStore.AppSettingsDocument
            {
                OrderHub = orderHub,
                PrintBridge = bridge
            });
            _settingsHolder.Replace(orderHub, bridge);
            MessageBox.Show("Ayarlar kaydedildi.", PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshDashboard();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ayarlar kaydedilemedi: {ex.Message}", PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
            MessageBox.Show(GetUserErrorMessage(ex), PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task TestConnectionAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var health = await _runtime.TestConnectionAsync(cts.Token).ConfigureAwait(true);
        var details = string.IsNullOrWhiteSpace(health.CustomerName)
            ? "Bağlantı başarılı."
            : $"Bağlantı başarılı.\nMüşteri: {health.CustomerName}\nCihaz: {health.DeviceName}";
        MessageBox.Show(details, PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task TestPrinterAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _runtime.TestPrinterAsync(cts.Token).ConfigureAwait(true);
        MessageBox.Show("Test çıktısı yazıcıya gönderildi.", PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenLogsFolder()
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
        if (!Directory.Exists(PrintBridgePaths.ProgramDataLogDirectory))
        {
            MessageBox.Show("Log klasörü henüz oluşturulmadı.", PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            MessageBox.Show(GetUserErrorMessage(ex), PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshDashboard()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshDashboard);
            return;
        }

        var status = _runtime.GetStatus();

        _deviceStatusValue.Text = status.DeviceStatusSummary;
        _deviceStatusValue.ForeColor = status.TrayIconState switch
        {
            TrayIconState.Printing => PrintBridgeUiTheme.Info,
            TrayIconState.Polling => PrintBridgeUiTheme.Info,
            TrayIconState.Connected => PrintBridgeUiTheme.Success,
            TrayIconState.ConnectionLost => PrintBridgeUiTheme.Danger,
            _ => PrintBridgeUiTheme.TextTitle
        };

        _lastPollMetricValue.Text = FormatUtc(status.LastPollUtc);
        _lastPollMetricValue.ForeColor = PrintBridgeUiTheme.TextTitle;
        _jobsTodayValue.Text = status.JobsTodayCount.ToString();
        _jobsTodayValue.ForeColor = PrintBridgeUiTheme.TextTitle;
        _failedTodayValue.Text = status.FailedTodayCount.ToString();
        _failedTodayValue.ForeColor = status.FailedTodayCount > 0
            ? PrintBridgeUiTheme.Danger
            : PrintBridgeUiTheme.TextTitle;

        UpdateHeaderBadge(status);
        UpdateStartStopButton(status);
    }

    private void RefreshRecentJobsFromRuntime()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshRecentJobsFromRuntime);
            return;
        }

        var status = _runtime.GetStatus();
        RefreshRecentJobs(status.RecentJobs, status.PrinterName);
    }

    private void RefreshLogs()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshLogs);
            return;
        }

        if (_txtLogs is null || _txtLogs.IsDisposed)
            return;

        var previousSelectionStart = _txtLogs.SelectionStart;
        var previousSelectionLength = _txtLogs.SelectionLength;
        var wasAtEnd = _txtLogs.SelectionStart >= Math.Max(0, _txtLogs.TextLength - 1);

        _txtLogs.Text = _uiLogBuffer.GetText();

        if (wasAtEnd)
        {
            _txtLogs.SelectionStart = _txtLogs.TextLength;
            _txtLogs.ScrollToCaret();
        }
        else
        {
            _txtLogs.SelectionStart = Math.Min(previousSelectionStart, _txtLogs.TextLength);
            _txtLogs.SelectionLength = Math.Min(previousSelectionLength, _txtLogs.TextLength - _txtLogs.SelectionStart);
        }
    }

    private void UpdateHeaderBadge(PrintBridgeRuntimeStatus status)
    {
        if (status.IsRunning && status.IsConnected)
        {
            _headerBadge.Text = status.DryRun ? "Çalışıyor (test)" : "Çalışıyor";
            _headerBadge.BackColor = PrintBridgeUiTheme.Info;
        }
        else if (status.IsRunning)
        {
            _headerBadge.Text = "Çalışıyor";
            _headerBadge.BackColor = PrintBridgeUiTheme.Info;
        }
        else if (status.IsConnected)
        {
            _headerBadge.Text = "Bağlı";
            _headerBadge.BackColor = PrintBridgeUiTheme.Success;
        }
        else
        {
            _headerBadge.Text = "Durduruldu";
            _headerBadge.BackColor = PrintBridgeUiTheme.Inactive;
        }
    }

    private void UpdateStartStopButton(PrintBridgeRuntimeStatus status)
    {
        _btnStartStop.Text = status.IsRunning ? "Dinlemeyi durdur" : "Dinlemeyi başlat";
        _btnStartStop.BackColor = status.IsRunning ? PrintBridgeUiTheme.Danger : PrintBridgeUiTheme.PrimaryButton;
        _btnStartStop.FlatAppearance.MouseOverBackColor = status.IsRunning
            ? Color.FromArgb(200, 45, 60)
            : PrintBridgeUiTheme.PrimaryButtonHover;
    }

    private void RefreshRecentJobs(IReadOnlyList<LocalPrintJobRecord> jobs, string printerName)
    {
        _jobsForGrid = jobs;
        _recentJobsEmptyLabel.Visible = jobs.Count == 0;
        _recentJobsGrid.Visible = jobs.Count > 0;

        var scrollIndex = _recentJobsGrid.FirstDisplayedScrollingRowIndex;

        _recentJobsGrid.SuspendLayout();
        try
        {
            _recentJobsGrid.Rows.Clear();
            foreach (var job in jobs)
            {
                _recentJobsGrid.Rows.Add(
                    FormatUtc(job.DisplayTimeUtc),
                    FormatJobLabel(job),
                    string.IsNullOrWhiteSpace(job.JobType) ? "-" : job.JobType,
                    string.IsNullOrWhiteSpace(printerName) ? "-" : printerName,
                    job.StatusBadgeText);
            }
        }
        finally
        {
            _recentJobsGrid.ResumeLayout();
        }

        if (scrollIndex >= 0 && scrollIndex < _recentJobsGrid.RowCount)
            _recentJobsGrid.FirstDisplayedScrollingRowIndex = scrollIndex;
    }

    private static string FormatJobLabel(LocalPrintJobRecord job) =>
        string.IsNullOrWhiteSpace(job.OrderDisplay)
            ? job.ShortJobId
            : $"{job.OrderDisplay} ({job.ShortJobId})";

    private static void OnRecentJobsCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || sender is not DataGridView grid)
            return;

        if (grid.Columns[e.ColumnIndex].Name != "Status" || e.Value is not string statusText || e.CellStyle is null)
            return;

        var (backColor, foreColor) = statusText switch
        {
            "Printed" => (PrintBridgeUiTheme.Success, Color.White),
            "Printing" => (PrintBridgeUiTheme.Info, Color.White),
            "Pending" => (Color.FromArgb(255, 193, 7), Color.FromArgb(33, 37, 41)),
            "Failed" => (PrintBridgeUiTheme.Danger, Color.White),
            "Skipped" => (PrintBridgeUiTheme.Inactive, Color.White),
            _ => (grid.DefaultCellStyle.BackColor, grid.DefaultCellStyle.ForeColor)
        };

        e.CellStyle.BackColor = backColor;
        e.CellStyle.ForeColor = foreColor;
        e.CellStyle.SelectionBackColor = backColor;
        e.CellStyle.SelectionForeColor = foreColor;
        e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        e.CellStyle.Font = PrintBridgeUiTheme.BadgeFont;
    }

    private void OnRecentJobsCellToolTipTextNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || sender is not DataGridView grid)
            return;

        if (grid.Columns[e.ColumnIndex].Name != "Status" || e.RowIndex >= _jobsForGrid.Count)
            return;

        var job = _jobsForGrid[e.RowIndex];
        if (job.Status == LocalPrintJobStatus.Failed && !string.IsNullOrWhiteSpace(job.ErrorMessage))
            e.ToolTipText = job.ErrorMessage;
    }

    private static string FormatUtc(DateTime? value) =>
        value.HasValue ? value.Value.ToLocalTime().ToString("g") : "-";

    private static string GetUserErrorMessage(Exception ex) =>
        ex is PrintBridgeConnectionException connectionEx
            ? connectionEx.UserMessage
            : ex.Message;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _dashboardTimer.Stop();
        _dashboardTimer.Dispose();
        _jobsRefreshTimer.Stop();
        _jobsRefreshTimer.Dispose();
        base.OnFormClosed(e);
    }
}
