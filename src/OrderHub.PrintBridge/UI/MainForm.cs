using Microsoft.Extensions.DependencyInjection;
using OrderHub.PrintBridge.Configuration;
using OrderHub.PrintBridge.Localization;
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
    private readonly PrintBridgeLocalizer _localizer;
    private readonly PrintBridgeCultureService _cultureService;
    private readonly System.Windows.Forms.Timer _dashboardTimer;
    private readonly System.Windows.Forms.Timer _jobsRefreshTimer;

    private TabControl _tabs = null!;
    private TabPage _statusTab = null!;
    private TabPage _logsTab = null!;
    private TabPage _settingsTab = null!;

    private Label _titleLabel = null!;
    private Label _subtitleLabel = null!;
    private Label _headerBadge = null!;
    private Label _deviceStatusTitle = null!;
    private Label _deviceStatusValue = null!;
    private Label _lastPollTitle = null!;
    private Label _lastPollMetricValue = null!;
    private Label _jobsTodayTitle = null!;
    private Label _jobsTodayValue = null!;
    private Label _failedTodayTitle = null!;
    private Label _failedTodayValue = null!;
    private GroupBox _jobsGroup = null!;
    private DataGridView _recentJobsGrid = null!;
    private Label _recentJobsEmptyLabel = null!;
    private IReadOnlyList<LocalPrintJobRecord> _jobsForGrid = Array.Empty<LocalPrintJobRecord>();
    private TextBox _txtLogs = null!;
    private Button _btnTestConnection = null!;
    private Button _btnTestPrinter = null!;
    private Button _btnStartStop = null!;
    private Button _btnOpenLogs = null!;
    private Button _btnClearLogs = null!;
    private Button _btnCopyLogs = null!;
    private Button _btnOpenLogsFolderTab = null!;

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
    private ComboBox _cmbLanguage = null!;
    private Label _lblLanguage = null!;
    private Label _lblServerUrl = null!;
    private Label _lblAgentToken = null!;
    private Label _lblPrinterName = null!;
    private Label _lblComputerName = null!;
    private GroupBox _advancedGroup = null!;
    private Label _lblDryRunMode = null!;
    private Label _lblIdlePoll = null!;
    private Label _lblBusyPoll = null!;
    private Label _lblErrorPoll = null!;
    private Label _settingsHint = null!;

    public MainForm(ServiceProvider services, PrintBridgeRuntime runtime)
    {
        _services = services;
        _runtime = runtime;
        _settingsStore = services.GetRequiredService<PrintBridgeSettingsStore>();
        _settingsHolder = services.GetRequiredService<PrintBridgeSettingsHolder>();
        _uiLogBuffer = services.GetRequiredService<UiLogBuffer>();
        _localizer = services.GetRequiredService<PrintBridgeLocalizer>();
        _cultureService = services.GetRequiredService<PrintBridgeCultureService>();

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
        _statusTab = new TabPage { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(12) };
        _logsTab = new TabPage { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(12) };
        _settingsTab = new TabPage { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(12) };
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
        PopulateLanguageCombo();
        LoadSettingsIntoForm();

        if (!IsHandleCreated)
            CreateHandle();

        ApplyStartupLocalization();
        RefreshDashboard();
        RefreshRecentJobsFromRuntime();
        RefreshLogs();
    }

    private void ApplyStartupLocalization()
    {
        Text = _localizer["Common.AppTitle"];
        _statusTab.Text = _localizer["Tab.Status"];
        _logsTab.Text = _localizer["Tab.Logs"];
        _settingsTab.Text = _localizer["Tab.Settings"];

        _titleLabel.Text = _localizer["Common.AppTitle"];
        _subtitleLabel.Text = _localizer["Common.Subtitle"];

        _deviceStatusTitle.Text = _localizer["Dashboard.DeviceStatus"];
        _lastPollTitle.Text = _localizer["Dashboard.LastPoll"];
        _jobsTodayTitle.Text = _localizer["Dashboard.JobsToday"];
        _failedTodayTitle.Text = _localizer["Dashboard.FailedToday"];

        _jobsGroup.Text = $"  {_localizer["RecentJobs.Title"]}  ";
        _recentJobsEmptyLabel.Text = _localizer["RecentJobs.Empty"];
        _recentJobsGrid.Columns["Time"]!.HeaderText = _localizer["RecentJobs.Column.Time"];
        _recentJobsGrid.Columns["Order"]!.HeaderText = _localizer["RecentJobs.Column.Order"];
        _recentJobsGrid.Columns["Type"]!.HeaderText = _localizer["RecentJobs.Column.Type"];
        _recentJobsGrid.Columns["Printer"]!.HeaderText = _localizer["RecentJobs.Column.Printer"];
        _recentJobsGrid.Columns["Status"]!.HeaderText = _localizer["RecentJobs.Column.Status"];

        _btnTestConnection.Text = _localizer["Button.TestConnection"];
        _btnTestPrinter.Text = _localizer["Button.TestPrinter"];
        _btnOpenLogs.Text = _localizer["Button.OpenLogsFolder"];
        _btnOpenLogsFolderTab.Text = _localizer["Button.OpenLogsFolder"];
        _btnClearLogs.Text = _localizer["Button.ClearLogs"];
        _btnCopyLogs.Text = _localizer["Button.CopyLogs"];
        _btnSaveSettings.Text = _localizer["Button.SaveSettings"];
        _btnRefreshPrinters.Text = _localizer["Button.Refresh"];
        _btnToggleToken.Text = _txtAgentToken.UseSystemPasswordChar
            ? _localizer["Button.ShowToken"]
            : _localizer["Button.HideToken"];

        _lblServerUrl.Text = _localizer["Settings.ServerUrl"];
        _lblAgentToken.Text = _localizer["Settings.AgentToken"];
        _lblPrinterName.Text = _localizer["Settings.PrinterName"];
        _lblComputerName.Text = _localizer["Settings.ComputerName"];
        _lblLanguage.Text = _localizer["Settings.Language"];
        _advancedGroup.Text = $"  {_localizer["Settings.Advanced"]}  ";
        _lblDryRunMode.Text = _localizer["Settings.DryRunMode"];
        _chkDryRun.Text = _localizer["Settings.DryRunDescription"];
        _lblIdlePoll.Text = _localizer["Settings.IdlePollSeconds"];
        _lblBusyPoll.Text = _localizer["Settings.BusyPollSeconds"];
        _lblErrorPoll.Text = _localizer["Settings.ErrorPollSeconds"];
        _settingsHint.Text = _localizer.GetString("Settings.SavedPathHint", PrintBridgePaths.ProgramDataConfigPath);

        PrintBridgeRtl.Apply(this, _cultureService.IsRightToLeft);
    }

    public void SelectStatusTab() => _tabs.SelectedTab = _statusTab;

    public void SelectLogsTab() => _tabs.SelectedTab = _logsTab;

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

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        };
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _titleLabel = new Label
        {
            Font = PrintBridgeUiTheme.TitleFont,
            ForeColor = PrintBridgeUiTheme.TextTitle,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 2)
        };
        _subtitleLabel = new Label
        {
            Font = PrintBridgeUiTheme.SubtitleFont,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6)
        };
        _headerBadge = new Label
        {
            Font = PrintBridgeUiTheme.BadgeFont,
            ForeColor = Color.White,
            BackColor = PrintBridgeUiTheme.Inactive,
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            Dock = DockStyle.Left,
            Margin = new Padding(0, 0, 0, 0)
        };
        header.Controls.Add(_titleLabel, 0, 0);
        header.Controls.Add(_subtitleLabel, 0, 1);
        header.Controls.Add(_headerBadge, 0, 2);
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

        var deviceStatusCard = PrintBridgeUiTheme.CreateMetricCard(string.Empty, out _deviceStatusValue, out _deviceStatusTitle);
        cardsRow.Controls.Add(deviceStatusCard, 0, 0);

        var lastPollCard = PrintBridgeUiTheme.CreateMetricCard(string.Empty, out _lastPollMetricValue, out _lastPollTitle);
        cardsRow.Controls.Add(lastPollCard, 1, 0);

        var jobsTodayCard = PrintBridgeUiTheme.CreateMetricCard(string.Empty, out _jobsTodayValue, out _jobsTodayTitle);
        cardsRow.Controls.Add(jobsTodayCard, 2, 0);

        var failedTodayCard = PrintBridgeUiTheme.CreateMetricCard(string.Empty, out _failedTodayValue, out _failedTodayTitle);
        cardsRow.Controls.Add(failedTodayCard, 3, 0);
        root.Controls.Add(cardsRow, 0, 1);

        _jobsGroup = new GroupBox
        {
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
        _recentJobsGrid.Columns.Add("Time", string.Empty);
        _recentJobsGrid.Columns.Add("Order", string.Empty);
        _recentJobsGrid.Columns.Add("Type", string.Empty);
        _recentJobsGrid.Columns.Add("Printer", string.Empty);
        _recentJobsGrid.Columns.Add("Status", string.Empty);
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
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Font = new Font("Segoe UI", 10F)
        };
        jobsPanel.Controls.Add(_recentJobsGrid);
        jobsPanel.Controls.Add(_recentJobsEmptyLabel);
        _jobsGroup.Controls.Add(jobsPanel);
        root.Controls.Add(_jobsGroup, 0, 2);

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
        _btnStartStop = PrintBridgeUiTheme.CreateActionButton(string.Empty, primary: true);
        _btnStartStop.Click += (_, _) => TogglePolling();
        buttonPanel.Controls.Add(_btnStartStop);

        _btnTestConnection = PrintBridgeUiTheme.CreateActionButton(string.Empty);
        _btnTestConnection.Click += async (_, _) => await RunSafeAsync(TestConnectionAsync);
        buttonPanel.Controls.Add(_btnTestConnection);

        _btnTestPrinter = PrintBridgeUiTheme.CreateActionButton(string.Empty);
        _btnTestPrinter.Click += async (_, _) => await RunSafeAsync(TestPrinterAsync);
        buttonPanel.Controls.Add(_btnTestPrinter);

        _btnOpenLogs = PrintBridgeUiTheme.CreateActionButton(string.Empty);
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

        _btnClearLogs = PrintBridgeUiTheme.CreateActionButton(string.Empty);
        _btnClearLogs.Click += (_, _) =>
        {
            _uiLogBuffer.Clear();
            RefreshLogs();
        };
        toolbar.Controls.Add(_btnClearLogs);

        _btnCopyLogs = PrintBridgeUiTheme.CreateActionButton(string.Empty);
        _btnCopyLogs.Click += (_, _) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_txtLogs.Text))
                    Clipboard.SetText(_txtLogs.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    _localizer.GetString("Message.LogsCopyFailed", ex.Message),
                    PrintBridgePaths.ProductDisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        };
        toolbar.Controls.Add(_btnCopyLogs);

        _btnOpenLogsFolderTab = PrintBridgeUiTheme.CreateActionButton(string.Empty);
        _btnOpenLogsFolderTab.Click += (_, _) => OpenLogsFolder();
        toolbar.Controls.Add(_btnOpenLogsFolderTab);

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

        (_txtBaseUrl, _lblServerUrl) = AddSettingsTextRow(settingsLayout, 0);
        AddTokenRow(settingsLayout, 1);
        AddPrinterRow(settingsLayout, 2);
        (_txtBridgeName, _lblComputerName) = AddSettingsTextRow(settingsLayout, 3);
        AddLanguageRow(settingsLayout, 4);

        _advancedGroup = new GroupBox
        {
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

        _lblDryRunMode = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        };
        advancedLayout.Controls.Add(_lblDryRunMode, 0, 0);
        _chkDryRun = new CheckBox
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        advancedLayout.Controls.Add(_chkDryRun, 1, 0);

        (_numIdlePoll, _lblIdlePoll) = AddSettingsNumericRow(advancedLayout, 1, 1, 300, 5);
        (_numBusyPoll, _lblBusyPoll) = AddSettingsNumericRow(advancedLayout, 2, 1, 60, 1);
        (_numErrorPoll, _lblErrorPoll) = AddSettingsNumericRow(advancedLayout, 3, 1, 300, 15);
        _advancedGroup.Controls.Add(advancedLayout);

        settingsLayout.Controls.Add(_advancedGroup, 0, 5);
        settingsLayout.SetColumnSpan(_advancedGroup, 2);

        var savePanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 12, 0, 0)
        };
        _btnSaveSettings = PrintBridgeUiTheme.CreateActionButton(string.Empty, primary: true);
        _btnSaveSettings.Click += (_, _) => SaveSettings();
        savePanel.Controls.Add(_btnSaveSettings);
        settingsLayout.Controls.Add(savePanel, 0, 6);
        settingsLayout.SetColumnSpan(savePanel, 2);

        scroll.Controls.Add(settingsLayout);

        _settingsHint = new Label
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Dock = DockStyle.Bottom,
            Padding = new Padding(4, 8, 4, 4)
        };

        var settingsHost = new Panel { Dock = DockStyle.Fill };
        settingsHost.Controls.Add(scroll);
        settingsHost.Controls.Add(_settingsHint);
        _settingsTab.Controls.Add(settingsHost);
    }

    private void AddLanguageRow(TableLayoutPanel table, int row)
    {
        _lblLanguage = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        };
        table.Controls.Add(_lblLanguage, 0, row);

        _cmbLanguage = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        table.Controls.Add(_cmbLanguage, 1, row);
    }

    private void AddTokenRow(TableLayoutPanel table, int row)
    {
        _lblAgentToken = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        };
        table.Controls.Add(_lblAgentToken, 0, row);

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
            AutoSize = true,
            MinimumSize = new Size(72, 28),
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(8, 0, 0, 0)
        };
        _btnToggleToken.Click += (_, _) =>
        {
            _txtAgentToken.UseSystemPasswordChar = !_txtAgentToken.UseSystemPasswordChar;
            _btnToggleToken.Text = _txtAgentToken.UseSystemPasswordChar
                ? _localizer["Button.ShowToken"]
                : _localizer["Button.HideToken"];
        };
        tokenPanel.Controls.Add(_txtAgentToken, 0, 0);
        tokenPanel.Controls.Add(_btnToggleToken, 1, 0);
        table.Controls.Add(tokenPanel, 1, row);
    }

    private void AddPrinterRow(TableLayoutPanel table, int row)
    {
        _lblPrinterName = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        };
        table.Controls.Add(_lblPrinterName, 0, row);

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

    private static (TextBox TextBox, Label Label) AddSettingsTextRow(TableLayoutPanel table, int row)
    {
        var label = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        };
        table.Controls.Add(label, 0, row);
        var textBox = new TextBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        table.Controls.Add(textBox, 1, row);
        return (textBox, label);
    }

    private static (NumericUpDown Numeric, Label Label) AddSettingsNumericRow(
        TableLayoutPanel table,
        int row,
        decimal min,
        decimal max,
        decimal value)
    {
        var label = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        };
        table.Controls.Add(label, 0, row);
        var numeric = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 110,
            Anchor = AnchorStyles.Left
        };
        table.Controls.Add(numeric, 1, row);
        return (numeric, label);
    }

    private void PopulateLanguageCombo()
    {
        _cmbLanguage.Items.Clear();
        _cmbLanguage.Items.Add(new LanguageOption(SupportedCultures.Turkish, "Türkçe"));
        _cmbLanguage.Items.Add(new LanguageOption(SupportedCultures.English, "English"));
        _cmbLanguage.Items.Add(new LanguageOption(SupportedCultures.Arabic, "العربية"));
    }

    private void SelectSavedLanguage()
    {
        var savedLanguage = _settingsHolder.Ui.Language;
        var selectedCulture = string.IsNullOrWhiteSpace(savedLanguage)
            ? _cultureService.CurrentCulture.Name
            : SupportedCultures.NormalizeOrDefault(savedLanguage);

        for (var i = 0; i < _cmbLanguage.Items.Count; i++)
        {
            if (_cmbLanguage.Items[i] is LanguageOption option &&
                string.Equals(option.CultureName, selectedCulture, StringComparison.OrdinalIgnoreCase))
            {
                _cmbLanguage.SelectedIndex = i;
                return;
            }
        }

        _cmbLanguage.SelectedIndex = 0;
    }

    private sealed record LanguageOption(string CultureName, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private void LoadSettingsIntoForm()
    {
        var (hub, bridge, ui) = _settingsHolder.Snapshot();
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
        SelectSavedLanguage();
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

        if (!PrintBridgeSettingsValidator.TryValidate(orderHub, bridge, out var errorKey))
        {
            MessageBox.Show(_localizer[errorKey!], PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var previousLanguage = _settingsHolder.Ui.Language;
        var ui = _settingsHolder.Ui;
        if (_cmbLanguage.SelectedItem is LanguageOption languageOption)
            ui.Language = languageOption.CultureName;

        var previousNormalized = string.IsNullOrWhiteSpace(previousLanguage)
            ? _cultureService.CurrentCulture.Name
            : SupportedCultures.NormalizeOrDefault(previousLanguage);
        var newNormalized = SupportedCultures.NormalizeOrDefault(ui.Language);
        var languageChanged = !string.Equals(previousNormalized, newNormalized, StringComparison.OrdinalIgnoreCase);

        try
        {
            _settingsStore.Save(new PrintBridgeSettingsStore.AppSettingsDocument
            {
                OrderHub = orderHub,
                PrintBridge = bridge,
                Ui = ui
            });
            _settingsHolder.Replace(orderHub, bridge, ui);

            var message = languageChanged
                ? _localizer["Message.LanguageRestartRequired"]
                : _localizer["Message.SettingsSaved"];
            MessageBox.Show(message, PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshDashboard();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                _localizer.GetString("Message.SettingsSaveFailed", ex.Message),
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
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
            ? _localizer["Message.ConnectionSuccess"]
            : _localizer.GetString(
                "Message.ConnectionSuccessWithDetails",
                Environment.NewLine,
                health.CustomerName,
                health.DeviceName);
        MessageBox.Show(details, PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task TestPrinterAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _runtime.TestPrinterAsync(cts.Token).ConfigureAwait(true);
        MessageBox.Show(_localizer["Message.TestPrintSent"], PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenLogsFolder()
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
        if (!Directory.Exists(PrintBridgePaths.ProgramDataLogDirectory))
        {
            MessageBox.Show(_localizer["Message.LogsFolderMissing"], PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        _deviceStatusValue.Text = _localizer.GetTrayIconState(status.TrayIconState);
        _deviceStatusValue.ForeColor = status.TrayIconState switch
        {
            TrayIconState.Printing => PrintBridgeUiTheme.Info,
            TrayIconState.Polling => PrintBridgeUiTheme.Info,
            TrayIconState.Connected => PrintBridgeUiTheme.Success,
            TrayIconState.ConnectionLost => PrintBridgeUiTheme.Danger,
            _ => PrintBridgeUiTheme.TextTitle
        };

        _lastPollMetricValue.Text = FormatUtc(status.LastPollUtc, _localizer["Common.Dash"]);
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
        _headerBadge.Text = _localizer.GetHeaderBadge(status);
        if (!status.IsRunning && status.IsConnected)
            _headerBadge.BackColor = PrintBridgeUiTheme.Success;
        else if (status.IsRunning)
            _headerBadge.BackColor = PrintBridgeUiTheme.Info;
        else
            _headerBadge.BackColor = PrintBridgeUiTheme.Inactive;
    }

    private void UpdateStartStopButton(PrintBridgeRuntimeStatus status)
    {
        _btnStartStop.Text = status.IsRunning
            ? _localizer["Button.StopListening"]
            : _localizer["Button.StartListening"];
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
                    FormatUtc(job.DisplayTimeUtc, _localizer["Common.Dash"]),
                    FormatJobLabel(job),
                    string.IsNullOrWhiteSpace(job.JobType) ? _localizer["Common.Dash"] : job.JobType,
                    string.IsNullOrWhiteSpace(printerName) ? _localizer["Common.Dash"] : printerName,
                    _localizer.GetJobStatus(job.Status));
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

    private void OnRecentJobsCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || sender is not DataGridView grid)
            return;

        if (grid.Columns[e.ColumnIndex].Name != "Status" || e.CellStyle is null || e.RowIndex >= _jobsForGrid.Count)
            return;

        var (backColor, foreColor) = _jobsForGrid[e.RowIndex].Status switch
        {
            LocalPrintJobStatus.Printed => (PrintBridgeUiTheme.Success, Color.White),
            LocalPrintJobStatus.Printing => (PrintBridgeUiTheme.Info, Color.White),
            LocalPrintJobStatus.Received => (Color.FromArgb(255, 193, 7), Color.FromArgb(33, 37, 41)),
            LocalPrintJobStatus.Failed => (PrintBridgeUiTheme.Danger, Color.White),
            LocalPrintJobStatus.Skipped => (PrintBridgeUiTheme.Inactive, Color.White),
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

    private static string FormatUtc(DateTime? value, string dash) =>
        value.HasValue ? value.Value.ToLocalTime().ToString("g") : dash;

    private string GetUserErrorMessage(Exception ex)
    {
        if (ex is PrintBridgeConnectionException connectionEx)
            return _localizer.GetString(connectionEx.UserMessageKey, connectionEx.FormatArgs);

        if (ex is LocalizedApplicationException localized)
            return _localizer.GetString(localized.ResourceKey, localized.Args);

        return ex.Message;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _dashboardTimer.Stop();
        _dashboardTimer.Dispose();
        _jobsRefreshTimer.Stop();
        _jobsRefreshTimer.Dispose();
        base.OnFormClosed(e);
    }
}
