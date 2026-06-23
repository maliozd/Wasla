using System.Drawing.Drawing2D;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.UI;

public sealed partial class MainForm : Form
{
    private readonly ServiceProvider _services;
    private readonly PrintBridgeRuntime _runtime;
    private readonly PrintBridgeSettingsStore _settingsStore;
    private readonly PrintBridgeSettingsHolder _settingsHolder;
    private readonly PrintBridgeDeviceMetadataSync _deviceMetadataSync;
    private readonly UiLogBuffer _uiLogBuffer;
    private readonly PrintBridgeLocalizer _localizer;
    private readonly PrintBridgeCultureService _cultureService;
    private readonly System.Windows.Forms.Timer _dashboardTimer;
    private readonly System.Windows.Forms.Timer _jobsRefreshTimer;

    private TabControl _tabs = null!;
    private TabPage _statusTab = null!;
    private TabPage _logsTab = null!;
    private TabPage _historyTab = null!;
    private TabPage _settingsTab = null!;
    private Panel _settingsScrollPanel = null!;
    private TableLayoutPanel _settingsLayout = null!;

    private Label _titleLabel = null!;
    private Label _subtitleLabel = null!;
    private Label _headerBadge = null!;
    private Label _footerDeviceLabel = null!;
    private Label _footerVersionLabel = null!;
    private Label _lastPrintTitle = null!;
    private Label _lastPrintValue = null!;
    private Label _jobsTodayTitle = null!;
    private Label _jobsTodayValue = null!;
    private Label _failedTodayTitle = null!;
    private Label _failedTodayValue = null!;
    private Label _serverStatusTitle = null!;
    private Label _serverStatusValue = null!;
    private Label _printerStatusTitle = null!;
    private Label _printerStatusValue = null!;
    private Label _lastContactTitle = null!;
    private Label _lastContactValue = null!;
    private GroupBox _jobsGroup = null!;
    private DataGridView _recentJobsGrid = null!;
    private Label _recentJobsEmptyLabel = null!;
    private IReadOnlyList<LocalPrintJobRecord> _jobsForGrid = Array.Empty<LocalPrintJobRecord>();
    private LogTextBox _txtLogs = null!;
    private Panel _logsHost = null!;
    private Button _btnJumpToLatest = null!;
    private string _displayedLogText = string.Empty;
    private bool _hasUnreadLogsBelow;
    private Button _btnTestConnection = null!;
    private Button _btnTestPrinter = null!;
    private Button _btnStartStop = null!;
    private Button _btnOpenLogs = null!;
    private Button _btnClearLogs = null!;
    private Button _btnCopyLogs = null!;
    private Button _btnOpenLogsFolderTab = null!;

    private TextBox _txtServerUrl = null!;
    private TextBox _txtAgentToken = null!;
    private TextBox _txtSetupCode = null!;
    private Button _btnToggleToken = null!;
    private ComboBox _cmbPrinterName = null!;
    private Button _btnRefreshPrinters = null!;
    private TextBox _txtDisplayName = null!;
    private Label _lblDeviceNameManaged = null!;
    private Label _lblMachineNameHint = null!;
    private CheckBox _chkDryRun = null!;
    private NumericUpDown _numIdlePoll = null!;
    private NumericUpDown _numBusyPoll = null!;
    private NumericUpDown _numErrorPoll = null!;
    private Button _btnSaveConnection = null!;
    private Button _btnTestSettingsConnection = null!;
    private Button _btnConnectSetupCode = null!;
    private Button _btnSavePrinter = null!;
    private Button _btnTestSettingsPrinter = null!;
    private Button _btnSaveDeviceName = null!;
    private Button _btnApplyLanguage = null!;
    private Button _btnSaveAdvanced = null!;
    private ComboBox _cmbLanguage = null!;
    private Label _lblLanguage = null!;
    private Label _lblConnectionSectionText = null!;
    private Label _lblPrinterSectionText = null!;
    private Label _lblDeviceSectionText = null!;
    private Label _lblLanguageSectionText = null!;
    private GroupBox _connectionGroup = null!;
    private GroupBox _printerGroup = null!;
    private GroupBox _deviceGroup = null!;
    private GroupBox _languageGroup = null!;
    private Label _lblConnectionStatus = null!;
    private Label _lblPrinterStatus = null!;
    private Label _lblDeviceStatus = null!;
    private Label _lblLanguageStatus = null!;
    private Label _lblAdvancedStatus = null!;
    private Label _lblServerUrl = null!;
    private Label _lblAgentToken = null!;
    private Label _lblSetupCode = null!;
    private Label _lblServerUrlHelp = null!;
    private Label _lblAgentTokenHelp = null!;
    private Label _lblSetupCodeHelp = null!;
    private Label _lblPrinterName = null!;
    private Label _lblDeviceName = null!;
    private GroupBox _advancedGroup = null!;
    private Label _lblDryRunMode = null!;
    private Label _lblDryRunWarning = null!;
    private Label _lblIdlePoll = null!;
    private Label _lblBusyPoll = null!;
    private Label _lblErrorPoll = null!;
    private Label _settingsHint = null!;
    private bool _suppressLanguageSelectionChanged;
    private bool _isSavingLanguage;

    public MainForm(ServiceProvider services, PrintBridgeRuntime runtime)
    {
        _services = services;
        _runtime = runtime;
        _settingsStore = services.GetRequiredService<PrintBridgeSettingsStore>();
        _settingsHolder = services.GetRequiredService<PrintBridgeSettingsHolder>();
        _deviceMetadataSync = services.GetRequiredService<PrintBridgeDeviceMetadataSync>();
        _uiLogBuffer = services.GetRequiredService<UiLogBuffer>();
        _localizer = services.GetRequiredService<PrintBridgeLocalizer>();
        _cultureService = services.GetRequiredService<PrintBridgeCultureService>();

        Text = PrintBridgePaths.ProductDisplayName;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = PrintBridgeUiTheme.PageBackground;

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9.5F),
            Padding = new Point(8, 6)
        };
        _statusTab = new TabPage { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(12) };
        _logsTab = new TabPage { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(12) };
        _historyTab = new TabPage { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(12) };
        _settingsTab = new TabPage { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(12) };
        _tabs.TabPages.Add(_statusTab);
        _tabs.TabPages.Add(_logsTab);
        _tabs.TabPages.Add(_historyTab);
        _tabs.TabPages.Add(_settingsTab);

        BuildFooter();
        Controls.Add(_tabs);
        Controls.Add(_footerPanel);

        BuildStatusTab();
        BuildLogsTab();
        BuildHistoryTab();
        BuildSettingsTab();

        PrintBridgeWindowLayout.ApplyStartup(this, _settingsHolder.Snapshot().Ui);

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
            else if (_tabs.SelectedTab == _historyTab)
                RefreshPrintHistory();
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
        _historyTab.Text = _localizer["Tab.PrintHistory"];
        _settingsTab.Text = _localizer["Tab.Settings"];

        _titleLabel.Text = _localizer["Common.AppTitle"];
        _subtitleLabel.Text = _localizer["Common.Subtitle"];

        _lastPrintTitle.Text = _localizer["Dashboard.LastPrint"];
        _jobsTodayTitle.Text = _localizer["Dashboard.JobsToday"];
        _failedTodayTitle.Text = _localizer["Dashboard.FailedToday"];
        _serverStatusTitle.Text = _localizer["Dashboard.ServerStatus"];
        _printerStatusTitle.Text = _localizer["Dashboard.PrinterStatus"];
        _lastContactTitle.Text = _localizer["Dashboard.LastContact"];

        ApplyPrintHistoryLocalization();

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
        _btnJumpToLatest.Text = _localizer["Button.JumpToLatest"];
        _btnSaveConnection.Text = _localizer["Button.SaveConnection"];
        _btnTestSettingsConnection.Text = _localizer["Button.TestConnection"];
        _btnConnectSetupCode.Text = _localizer["Button.ConnectWithSetupCode"];
        _btnSavePrinter.Text = _localizer["Button.SavePrinter"];
        _btnTestSettingsPrinter.Text = _localizer["Button.TestPrinter"];
        _btnSaveDeviceName.Text = _localizer["Button.SaveDeviceName"];
        _btnApplyLanguage.Text = _localizer["Button.ApplyLanguage"];
        _btnSaveAdvanced.Text = _localizer["Button.SaveAdvanced"];
        _btnRefreshPrinters.Text = _localizer["Button.Refresh"];
        _btnToggleToken.Text = _txtAgentToken.UseSystemPasswordChar
            ? _localizer["Button.ShowToken"]
            : _localizer["Button.HideToken"];

        _lblServerUrl.Text = _localizer["Settings.ServerUrl"];
        _lblServerUrlHelp.Text = _localizer["Settings.ServerUrlHelp"];
        _lblAgentToken.Text = _localizer["Settings.AgentToken"];
        _lblAgentTokenHelp.Text = _localizer["Settings.AgentTokenHelp"];
        _lblSetupCode.Text = _localizer["Settings.SetupCode"];
        _lblSetupCodeHelp.Text = _localizer["Settings.SetupCodeHelp"];
        _lblPrinterName.Text = _localizer["Settings.PrinterName"];
        _lblDeviceName.Text = _localizer["Settings.DeviceName"];
        _lblDeviceNameManaged.Text = _localizer["Settings.DeviceNameLocalHelp"];
        _lblLanguage.Text = _localizer["Settings.Language"];
        _advancedGroup.Text = $"  {_localizer["Settings.Advanced"]}  ";
        _connectionGroup.Text = $"  {_localizer["Settings.Section.Connection"]}  ";
        _printerGroup.Text = $"  {_localizer["Settings.Section.Printer"]}  ";
        _deviceGroup.Text = $"  {_localizer["Settings.Section.Device"]}  ";
        _languageGroup.Text = $"  {_localizer["Settings.Section.Language"]}  ";
        _lblConnectionSectionText.Text = _localizer["Settings.Section.ConnectionHelp"];
        _lblPrinterSectionText.Text = _localizer["Settings.Section.PrinterHelp"];
        _lblDeviceSectionText.Text = _localizer["Settings.Section.DeviceHelp"];
        _lblLanguageSectionText.Text = _localizer["Settings.Section.LanguageHelp"];
        _lblDryRunMode.Text = _localizer["Settings.DryRunMode"];
        _chkDryRun.Text = _localizer["Settings.DryRunDescription"];
        if (_lblDryRunWarning != null)
            _lblDryRunWarning.Text = _localizer["Settings.DryRunEnabledWarning"];
        UpdateDryRunWarning();
        _lblIdlePoll.Text = _localizer["Settings.IdlePollSeconds"];
        _lblBusyPoll.Text = _localizer["Settings.BusyPollSeconds"];
        _lblErrorPoll.Text = _localizer["Settings.ErrorPollSeconds"];
        _settingsHint.Text = _localizer.GetString("Settings.SavedPathHint", PrintBridgePaths.ProgramDataConfigPath);

        var machineName = _settingsHolder.Snapshot().Bridge.MachineName;
        if (string.IsNullOrWhiteSpace(machineName))
            machineName = Environment.MachineName;
        _lblMachineNameHint.Text = _localizer.GetString("Settings.MachineNameHint", machineName);

        var isRtl = _cultureService.IsRightToLeft;
        _footerDeviceLabel.TextAlign = isRtl ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
        _footerVersionLabel.TextAlign = isRtl ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight;
        PrintBridgeRtl.Apply(this, isRtl);
        UpdateSettingsScrollLayout();
    }

    public void SelectStatusTab() => _tabs.SelectedTab = _statusTab;

    public void SelectLogsTab() => _tabs.SelectedTab = _logsTab;

    private Panel _footerPanel = null!;

    private void BuildFooter()
    {
        _footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Padding = new Padding(12, 6, 12, 6),
            BackColor = PrintBridgeUiTheme.PageBackground
        };

        var footerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var footerFont = new Font("Segoe UI", 8.25F);
        _footerDeviceLabel = new Label
        {
            Dock = DockStyle.Fill,
            Font = footerFont,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        _footerVersionLabel = new Label
        {
            Dock = DockStyle.Fill,
            Font = footerFont,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true
        };

        footerLayout.Controls.Add(_footerDeviceLabel, 0, 0);
        footerLayout.Controls.Add(_footerVersionLabel, 1, 0);
        _footerPanel.Controls.Add(footerLayout);
    }

    public void SelectPrintHistoryTab()
    {
        _tabs.SelectedTab = _historyTab;
        RefreshPrintHistory();
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

        var metricsRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        };
        metricsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15F));
        metricsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15F));
        metricsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
        metricsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12F));
        metricsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12F));
        metricsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26F));

        metricsRow.Controls.Add(PrintBridgeUiTheme.CreateMetricCard(string.Empty, out _serverStatusValue, out _serverStatusTitle), 0, 0);
        metricsRow.Controls.Add(PrintBridgeUiTheme.CreateMetricCard(string.Empty, out _printerStatusValue, out _printerStatusTitle), 1, 0);

        var lastContactCard = PrintBridgeUiTheme.CreateMetricCard(
            string.Empty,
            out _lastContactValue,
            out _lastContactTitle,
            PrintBridgeUiTheme.MetricTimestampFont,
            valueAutoEllipsis: false);
        metricsRow.Controls.Add(lastContactCard, 2, 0);

        metricsRow.Controls.Add(PrintBridgeUiTheme.CreateMetricCard(string.Empty, out _jobsTodayValue, out _jobsTodayTitle), 3, 0);
        metricsRow.Controls.Add(PrintBridgeUiTheme.CreateMetricCard(string.Empty, out _failedTodayValue, out _failedTodayTitle), 4, 0);

        var lastPrintCard = PrintBridgeUiTheme.CreateMetricCard(
            string.Empty,
            out _lastPrintValue,
            out _lastPrintTitle,
            PrintBridgeUiTheme.MetricTimestampFont,
            valueAutoEllipsis: false);
        metricsRow.Controls.Add(lastPrintCard, 5, 0);
        root.Controls.Add(metricsRow, 0, 1);

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
        _recentJobsGrid.Columns["Time"]!.FillWeight = 20;
        _recentJobsGrid.Columns["Time"]!.MinimumWidth = 128;
        _recentJobsGrid.Columns["Order"]!.FillWeight = 34;
        _recentJobsGrid.Columns["Order"]!.MinimumWidth = 160;
        _recentJobsGrid.Columns["Type"]!.FillWeight = 12;
        _recentJobsGrid.Columns["Type"]!.MinimumWidth = 72;
        _recentJobsGrid.Columns["Printer"]!.FillWeight = 20;
        _recentJobsGrid.Columns["Printer"]!.MinimumWidth = 100;
        _recentJobsGrid.Columns["Status"]!.FillWeight = 16;
        _recentJobsGrid.Columns["Status"]!.MinimumWidth = 112;
        _recentJobsGrid.CellPainting += OnRecentJobsCellPainting;
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
            _displayedLogText = string.Empty;
            _hasUnreadLogsBelow = false;
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

        _logsHost = new Panel
        {
            Dock = DockStyle.Fill
        };

        _txtLogs = new LogTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            BackColor = Color.FromArgb(248, 249, 250),
            BorderStyle = BorderStyle.FixedSingle
        };
        _txtLogs.UserScrolled += (_, _) => OnLogViewerScrolled();

        _btnJumpToLatest = PrintBridgeUiTheme.CreateActionButton(string.Empty);
        _btnJumpToLatest.Visible = false;
        _btnJumpToLatest.AutoSize = true;
        _btnJumpToLatest.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _btnJumpToLatest.Click += (_, _) => JumpToLatestLogs();
        _logsHost.Controls.Add(_txtLogs);
        _logsHost.Controls.Add(_btnJumpToLatest);
        _logsHost.Resize += (_, _) => PositionJumpToLatestButton();

        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(_logsHost, 0, 1);
        _logsTab.Controls.Add(root);
    }

    private void OnLogViewerScrolled()
    {
        if (TextBoxScrollHelper.IsNearBottom(_txtLogs))
        {
            _hasUnreadLogsBelow = false;
            UpdateJumpToLatestButton();
        }
    }

    private void JumpToLatestLogs()
    {
        TextBoxScrollHelper.ScrollToBottom(_txtLogs);
        _hasUnreadLogsBelow = false;
        UpdateJumpToLatestButton();
    }

    private void UpdateJumpToLatestButton()
    {
        if (_btnJumpToLatest.IsDisposed)
            return;

        var show = _hasUnreadLogsBelow && !TextBoxScrollHelper.IsNearBottom(_txtLogs);
        _btnJumpToLatest.Visible = show;
        if (show)
            PositionJumpToLatestButton();
    }

    private void PositionJumpToLatestButton()
    {
        if (_btnJumpToLatest.IsDisposed || _logsHost.IsDisposed)
            return;

        const int margin = 10;
        _btnJumpToLatest.Location = new Point(
            Math.Max(margin, _logsHost.ClientSize.Width - _btnJumpToLatest.Width - margin),
            Math.Max(margin, _logsHost.ClientSize.Height - _btnJumpToLatest.Height - margin));
        _btnJumpToLatest.BringToFront();
    }

    private void BuildSettingsTab()
    {
        _settingsScrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        _settingsLayout = new TableLayoutPanel
        {
            Location = new Point(0, 0),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            AutoSize = false,
            Padding = new Padding(4),
            ColumnCount = 1,
            RowCount = 0,
            MinimumSize = new Size(760, 0)
        };
        _settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _settingsScrollPanel.Resize += (_, _) => UpdateSettingsScrollLayout();
        _settingsLayout.SizeChanged += (_, _) => UpdateSettingsScrollLayout();
        _settingsLayout.Layout += (_, _) => UpdateSettingsScrollLayout();

        var connectionLayout = CreateSettingsSection(
            "Settings.Section.Connection",
            out _lblConnectionSectionText,
            out _lblConnectionStatus);
        AddServerUrlRow(connectionLayout, 1);
        AddTokenRow(connectionLayout, 2);
        AddSetupCodeRow(connectionLayout, 3);
        _btnSaveConnection = PrintBridgeUiTheme.CreateActionButton(string.Empty, primary: true);
        _btnSaveConnection.Click += async (_, _) => await SaveConnectionSettingsAsync().ConfigureAwait(true);
        _btnTestSettingsConnection = PrintBridgeUiTheme.CreateActionButton(string.Empty);
        _btnTestSettingsConnection.Click += async (_, _) => await TestConnectionFromSettingsAsync().ConfigureAwait(true);
        _btnConnectSetupCode = PrintBridgeUiTheme.CreateActionButton(string.Empty);
        _btnConnectSetupCode.Click += async (_, _) => await ConnectWithSetupCodeAsync().ConfigureAwait(true);
        AddSectionActions(connectionLayout, 4, _btnSaveConnection, _btnTestSettingsConnection, _btnConnectSetupCode);
        connectionLayout.Controls.Add(_lblConnectionStatus, 1, 5);
        _connectionGroup = WrapSettingsSection(connectionLayout, "Settings.Section.Connection");
        AddSettingsSection(_settingsLayout, _connectionGroup);

        var printerLayout = CreateSettingsSection(
            "Settings.Section.Printer",
            out _lblPrinterSectionText,
            out _lblPrinterStatus);
        AddPrinterRow(printerLayout, 1);
        _btnSavePrinter = PrintBridgeUiTheme.CreateActionButton(string.Empty, primary: true);
        _btnSavePrinter.Click += async (_, _) => await SavePrinterSettingsAsync().ConfigureAwait(true);
        _btnTestSettingsPrinter = PrintBridgeUiTheme.CreateActionButton(string.Empty);
        _btnTestSettingsPrinter.Click += async (_, _) => await TestPrinterFromSettingsAsync().ConfigureAwait(true);
        AddSectionActions(printerLayout, 2, _btnSavePrinter, _btnTestSettingsPrinter);
        printerLayout.Controls.Add(_lblPrinterStatus, 1, 3);
        _printerGroup = WrapSettingsSection(printerLayout, "Settings.Section.Printer");
        AddSettingsSection(_settingsLayout, _printerGroup);

        var deviceLayout = CreateSettingsSection(
            "Settings.Section.Device",
            out _lblDeviceSectionText,
            out _lblDeviceStatus);
        AddDeviceNameRow(deviceLayout, 1);
        _btnSaveDeviceName = PrintBridgeUiTheme.CreateActionButton(string.Empty, primary: true);
        _btnSaveDeviceName.Click += async (_, _) => await SaveDeviceIdentitySettingsAsync().ConfigureAwait(true);
        AddSectionActions(deviceLayout, 2, _btnSaveDeviceName);
        deviceLayout.Controls.Add(_lblDeviceStatus, 1, 3);
        _deviceGroup = WrapSettingsSection(deviceLayout, "Settings.Section.Device");
        AddSettingsSection(_settingsLayout, _deviceGroup);

        var languageLayout = CreateSettingsSection(
            "Settings.Section.Language",
            out _lblLanguageSectionText,
            out _lblLanguageStatus);
        AddLanguageRow(languageLayout, 1);
        _btnApplyLanguage = PrintBridgeUiTheme.CreateActionButton(string.Empty, primary: true);
        _btnApplyLanguage.Click += async (_, _) => await SaveSelectedLanguageAsync().ConfigureAwait(true);
        AddSectionActions(languageLayout, 2, _btnApplyLanguage);
        languageLayout.Controls.Add(_lblLanguageStatus, 1, 3);
        _languageGroup = WrapSettingsSection(languageLayout, "Settings.Section.Language");
        AddSettingsSection(_settingsLayout, _languageGroup);

        _advancedGroup = new GroupBox
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = PrintBridgeUiTheme.SectionFont,
            ForeColor = PrintBridgeUiTheme.TextTitle,
            Padding = new Padding(12, 18, 12, 12),
            Margin = new Padding(0, 0, 0, 10),
            MinimumSize = new Size(720, 0)
        };
        var advancedLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
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

        var dryRunPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            Margin = new Padding(0)
        };
        dryRunPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dryRunPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _chkDryRun = new CheckBox
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        _chkDryRun.CheckedChanged += (_, _) => UpdateDryRunWarning();
        _lblDryRunWarning = new Label
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.Warning,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 4, 0, 0),
            Visible = false
        };
        dryRunPanel.Controls.Add(_chkDryRun, 0, 0);
        dryRunPanel.Controls.Add(_lblDryRunWarning, 0, 1);
        advancedLayout.Controls.Add(dryRunPanel, 1, 0);

        (_numIdlePoll, _lblIdlePoll) = AddSettingsNumericRow(advancedLayout, 1, 1, 300, 5);
        (_numBusyPoll, _lblBusyPoll) = AddSettingsNumericRow(advancedLayout, 2, 1, 60, 1);
        (_numErrorPoll, _lblErrorPoll) = AddSettingsNumericRow(advancedLayout, 3, 1, 300, 15);
        _btnSaveAdvanced = PrintBridgeUiTheme.CreateActionButton(string.Empty, primary: true);
        _btnSaveAdvanced.Click += async (_, _) => await SaveAdvancedSettingsAsync().ConfigureAwait(true);
        _lblAdvancedStatus = CreateSectionStatusLabel();
        AddSectionActions(advancedLayout, 4, _btnSaveAdvanced);
        advancedLayout.Controls.Add(_lblAdvancedStatus, 1, 5);
        _advancedGroup.Controls.Add(advancedLayout);
        AddSettingsSection(_settingsLayout, _advancedGroup);
        InitializeSettingsSaveUi();
        UpdateSettingsScrollLayout();

        _settingsHint = new Label
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Dock = DockStyle.Bottom,
            Padding = new Padding(4, 8, 4, 4)
        };

        var settingsHost = new Panel { Dock = DockStyle.Fill };
        _settingsScrollPanel.Controls.Add(_settingsLayout);
        settingsHost.Controls.Add(_settingsScrollPanel);
        settingsHost.Controls.Add(_settingsHint);
        _settingsTab.Controls.Add(settingsHost);

        WireConnectionSettingsChangeHandlers();
    }

    private void UpdateSettingsScrollLayout()
    {
        if (_settingsScrollPanel is null || _settingsLayout is null)
            return;

        var availableWidth = Math.Max(
            _settingsLayout.MinimumSize.Width,
            _settingsScrollPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 12);

        foreach (Control section in _settingsLayout.Controls)
        {
            var sectionWidth = Math.Max(section.MinimumSize.Width, availableWidth - _settingsLayout.Padding.Horizontal);
            if (section.Width != sectionWidth)
                section.Width = sectionWidth;
        }

        var preferredHeight = _settingsLayout.GetPreferredSize(new Size(availableWidth, 0)).Height + 12;
        if (_settingsLayout.Width != availableWidth || _settingsLayout.Height != preferredHeight)
            _settingsLayout.Size = new Size(availableWidth, preferredHeight);

        _settingsScrollPanel.AutoScrollMinSize = _settingsLayout.Size;
    }

    private static void AddSettingsSection(TableLayoutPanel root, Control section)
    {
        var row = root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.Dock = DockStyle.Top;
        section.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        root.Controls.Add(section, 0, row);
    }

    private GroupBox WrapSettingsSection(TableLayoutPanel layout, string titleKey)
    {
        var group = new GroupBox
        {
            Text = $"  {_localizer[titleKey]}  ",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = PrintBridgeUiTheme.SectionFont,
            ForeColor = PrintBridgeUiTheme.TextTitle,
            Padding = new Padding(12, 18, 12, 12),
            Margin = new Padding(0, 0, 0, 10),
            MinimumSize = new Size(720, 0)
        };
        group.Controls.Add(layout);
        return group;
    }

    private TableLayoutPanel CreateSettingsSection(
        string titleKey,
        out Label descriptionLabel,
        out Label statusLabel)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        descriptionLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(0, 0, 0, 10)
        };
        table.Controls.Add(descriptionLabel, 0, 0);
        table.SetColumnSpan(descriptionLabel, 2);

        statusLabel = CreateSectionStatusLabel();

        return table;
    }

    private static Label CreateSectionStatusLabel() =>
        new()
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Visible = false,
            Margin = new Padding(8, 8, 0, 0)
        };

    private static void AddSectionActions(TableLayoutPanel table, int row, params Button[] buttons)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 10, 0, 0)
        };
        foreach (var button in buttons)
            panel.Controls.Add(button);

        table.Controls.Add(panel, 1, row);
    }

    private void AddDeviceNameRow(TableLayoutPanel table, int row)
    {
        _lblDeviceName = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        };
        table.Controls.Add(_lblDeviceName, 0, row);

        var fieldPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true
        };
        _txtDisplayName = new TextBox
        {
            Dock = DockStyle.Fill,
            MaxLength = 200,
            ReadOnly = false
        };
        _lblDeviceNameManaged = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Font = new Font("Segoe UI", 8.25F),
            Margin = new Padding(0, 4, 0, 0)
        };
        _lblMachineNameHint = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Font = new Font("Segoe UI", 8.25F),
            Margin = new Padding(0, 2, 0, 0)
        };
        fieldPanel.Controls.Add(_txtDisplayName, 0, 0);
        fieldPanel.Controls.Add(_lblDeviceNameManaged, 0, 1);
        fieldPanel.Controls.Add(_lblMachineNameHint, 0, 2);
        table.Controls.Add(fieldPanel, 1, row);
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
        _cmbLanguage.SelectedIndexChanged += (_, _) => SetSectionStatus(_lblLanguageStatus, null);
        table.Controls.Add(_cmbLanguage, 1, row);
    }

    private void AddServerUrlRow(TableLayoutPanel table, int row)
    {
        _lblServerUrl = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        };
        table.Controls.Add(_lblServerUrl, 0, row);

        var fieldPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            Margin = new Padding(0)
        };
        fieldPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fieldPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _txtServerUrl = new TextBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _lblServerUrlHelp = new Label
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 4, 0, 0)
        };

        fieldPanel.Controls.Add(_txtServerUrl, 0, 0);
        fieldPanel.Controls.Add(_lblServerUrlHelp, 0, 1);
        table.Controls.Add(fieldPanel, 1, row);
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

        var fieldPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            Margin = new Padding(0)
        };
        fieldPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fieldPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _lblAgentTokenHelp = new Label
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 4, 0, 0)
        };
        fieldPanel.Controls.Add(tokenPanel, 0, 0);
        fieldPanel.Controls.Add(_lblAgentTokenHelp, 0, 1);
        table.Controls.Add(fieldPanel, 1, row);
    }

    private void AddSetupCodeRow(TableLayoutPanel table, int row)
    {
        _lblSetupCode = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = PrintBridgeUiTheme.TextMuted
        };
        table.Controls.Add(_lblSetupCode, 0, row);

        var fieldPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            Margin = new Padding(0)
        };
        fieldPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fieldPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _txtSetupCode = new TextBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _lblSetupCodeHelp = new Label
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 4, 0, 0)
        };

        fieldPanel.Controls.Add(_txtSetupCode, 0, 0);
        fieldPanel.Controls.Add(_lblSetupCodeHelp, 0, 1);
        table.Controls.Add(fieldPanel, 1, row);
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
        QueueUiAction(SyncDeviceNameFieldFromHolder);
        QueueRefreshRecentJobs();
        QueueUiAction(() =>
        {
            if (_tabs.SelectedTab == _historyTab)
                RefreshPrintHistory();
        });
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

    public void RefreshAfterAutomaticSetup()
    {
        QueueUiAction(() =>
        {
            try
            {
                LoadSettingsIntoForm();
                RefreshDashboard();
                RefreshRecentJobsFromRuntime();
                UpdateSettingsScrollLayout();
            }
            catch (Exception ex)
            {
                _settingsLogger?.LogWarning(ex, "Automatic setup settings refresh could not update the open window.");
            }
        });
    }

    public void FocusPrinterSettingsSection()
    {
        SelectSettingsTab();
        _printerGroup.Focus();
        SetSectionStatus(
            _lblPrinterStatus,
            _localizer["Auto.ConnectedPrinterMissing"],
            isError: true);
    }

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
        _cmbLanguage.Items.Add(new LanguageOption(SupportedCultures.Russian, "Русский"));
    }

    private void SelectSavedLanguage()
    {
        var savedLanguage = _settingsHolder.Ui.Language;
        var selectedCulture = string.IsNullOrWhiteSpace(savedLanguage)
            ? _cultureService.CurrentCulture.Name
            : SupportedCultures.NormalizeOrDefault(savedLanguage);

        _suppressLanguageSelectionChanged = true;
        try
        {
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
        finally
        {
            _suppressLanguageSelectionChanged = false;
        }
    }

    private async Task SaveSelectedLanguageAsync()
    {
        if (_suppressLanguageSelectionChanged || _isSavingLanguage)
            return;

        if (_cmbLanguage.SelectedItem is not LanguageOption languageOption)
            return;

        var selectedCulture = SupportedCultures.NormalizeOrDefault(languageOption.CultureName);
        var previousCulture = _cultureService.CurrentCulture.Name;
        if (string.Equals(previousCulture, selectedCulture, StringComparison.OrdinalIgnoreCase))
            return;

        _isSavingLanguage = true;
        _cmbLanguage.Enabled = false;
        _btnApplyLanguage.Enabled = false;
        SetSectionStatus(_lblLanguageStatus, _localizer["Settings.Saving"]);
        try
        {
            await Task.Run(() => _settingsStore.SaveLanguage(selectedCulture)).ConfigureAwait(true);

            var ui = CloneUiOptions(_settingsHolder.Ui);
            ui.Language = selectedCulture;
            _settingsHolder.ReplaceUi(ui);
            _cultureService.Initialize(selectedCulture);
            ApplyStartupLocalization();
            RefreshDashboard();
            RefreshRecentJobsFromRuntime();
            if (_tabs.SelectedTab == _historyTab)
                RefreshPrintHistory();
            SetSectionStatus(_lblLanguageStatus, _localizer["Settings.LanguageApplied"], isSuccess: true);
        }
        catch (Exception ex)
        {
            _cultureService.Initialize(previousCulture);
            ResetLanguageSelection(previousCulture);
            SetSectionStatus(_lblLanguageStatus, _localizer.GetString("Message.SettingsSaveFailed", "Language"), isError: true);
            _settingsLogger?.LogWarning(ex, "Language setting could not be saved.");
        }
        finally
        {
            _cmbLanguage.Enabled = true;
            _btnApplyLanguage.Enabled = true;
            _isSavingLanguage = false;
        }
    }

    private void ResetLanguageSelection(string cultureName)
    {
        _suppressLanguageSelectionChanged = true;
        try
        {
            var normalized = SupportedCultures.NormalizeOrDefault(cultureName);
            for (var i = 0; i < _cmbLanguage.Items.Count; i++)
            {
                if (_cmbLanguage.Items[i] is LanguageOption option &&
                    string.Equals(option.CultureName, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _cmbLanguage.SelectedIndex = i;
                    return;
                }
            }
        }
        finally
        {
            _suppressLanguageSelectionChanged = false;
        }
    }

    private sealed record LanguageOption(string CultureName, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private void SyncDeviceNameFieldFromHolder()
    {
        if (_txtDisplayName is null)
            return;

        var bridge = _settingsHolder.Snapshot().Bridge;
        _txtDisplayName.Text = bridge.BridgeName ?? string.Empty;
    }

    private void LoadSettingsIntoForm()
    {
        var (hub, bridge, ui) = _settingsHolder.Snapshot();
        _txtServerUrl.Text = hub.ServerUrl;
        _txtAgentToken.Text = hub.AgentToken;
        RefreshPrinterList(bridge.PrinterName);
        _txtDisplayName.Text = bridge.BridgeName ?? string.Empty;
        _lblMachineNameHint.Text = _localizer.GetString(
            "Settings.MachineNameHint",
            string.IsNullOrWhiteSpace(bridge.MachineName) ? Environment.MachineName : bridge.MachineName);
        _chkDryRun.Checked = bridge.DryRun;
        UpdateDryRunWarning();
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
        SyncDeviceNameFieldFromHolder();
        var details = string.IsNullOrWhiteSpace(health.CustomerName)
            ? _localizer["Message.ConnectionSuccess"]
            : _localizer.GetString(
                "Message.ConnectionSuccessWithDetails",
                Environment.NewLine,
                health.CustomerName,
                health.DeviceName);
        if (!string.IsNullOrWhiteSpace(health.DeviceName))
            details += $"{Environment.NewLine}{_localizer["Message.DeviceNameLoadedFromServer"]}";
        MessageBox.Show(details, PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        RefreshDashboard();
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

        _footerDeviceLabel.Text = _localizer.GetString(
            "Footer.Device",
            _localizer.GetFooterDeviceName(status));
        _footerVersionLabel.Text = _localizer.GetString("Footer.Version", status.AppVersion);

        _lastPrintValue.Text = FormatUtc(status.LastPrintTimeUtc, _localizer["Common.Dash"]);
        _lastPrintValue.ForeColor = PrintBridgeUiTheme.TextTitle;
        _jobsTodayValue.Text = status.JobsTodayCount.ToString();
        _jobsTodayValue.ForeColor = PrintBridgeUiTheme.TextTitle;
        _failedTodayValue.Text = status.FailedTodayCount.ToString();
        _failedTodayValue.ForeColor = status.FailedTodayCount > 0
            ? PrintBridgeUiTheme.Danger
            : PrintBridgeUiTheme.TextTitle;

        _serverStatusValue.Text = _localizer.GetServerConnectionStatus(status.ServerConnectionStatus);
        _serverStatusValue.ForeColor = status.ServerConnectionStatus switch
        {
            BridgeServerConnectionStatus.Connected => PrintBridgeUiTheme.Success,
            BridgeServerConnectionStatus.Disconnected => PrintBridgeUiTheme.Warning,
            BridgeServerConnectionStatus.Error => PrintBridgeUiTheme.Danger,
            _ => PrintBridgeUiTheme.Inactive
        };

        _printerStatusValue.Text = _localizer.GetPrinterHealthStatus(status.PrinterHealthStatus);
        _printerStatusValue.ForeColor = status.PrinterHealthStatus switch
        {
            PrinterHealthStatus.Ready => PrintBridgeUiTheme.Success,
            PrinterHealthStatus.DryRun => PrintBridgeUiTheme.Info,
            PrinterHealthStatus.NotFound => PrintBridgeUiTheme.Danger,
            _ => PrintBridgeUiTheme.Warning
        };

        _lastContactValue.Text = FormatUtc(status.LastSuccessfulContactUtc, _localizer["Common.Dash"]);
        _lastContactValue.ForeColor = status.IsConnected
            ? PrintBridgeUiTheme.Success
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

        var newText = _uiLogBuffer.GetText();
        if (string.Equals(newText, _displayedLogText, StringComparison.Ordinal))
        {
            UpdateJumpToLatestButton();
            return;
        }

        var wasNearBottom = TextBoxScrollHelper.IsNearBottom(_txtLogs);
        var firstVisibleLine = TextBoxScrollHelper.GetFirstVisibleLine(_txtLogs);
        var hadNewContent = newText.Length > _displayedLogText.Length;

        if (!string.IsNullOrEmpty(_displayedLogText)
            && newText.StartsWith(_displayedLogText, StringComparison.Ordinal)
            && newText.Length > _displayedLogText.Length)
        {
            _txtLogs.AppendText(newText[_displayedLogText.Length..]);
        }
        else
        {
            _txtLogs.Text = newText;
            if (!wasNearBottom)
                TextBoxScrollHelper.RestoreFirstVisibleLine(_txtLogs, firstVisibleLine);
        }

        _displayedLogText = newText;

        if (wasNearBottom)
        {
            TextBoxScrollHelper.ScrollToBottom(_txtLogs);
            _hasUnreadLogsBelow = false;
        }
        else if (hadNewContent)
        {
            _hasUnreadLogsBelow = true;
        }

        UpdateJumpToLatestButton();
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
                    _localizer.GetJobType(job.JobType),
                    string.IsNullOrWhiteSpace(printerName) ? _localizer["Common.Dash"] : printerName,
                    _localizer.GetJobStatusBadge(job.Status));
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
        string.IsNullOrWhiteSpace(job.OrderDisplay) ? job.ShortJobId : job.OrderDisplay;

    private string FormatJobOrderTooltip(LocalPrintJobRecord job)
    {
        if (string.IsNullOrWhiteSpace(job.OrderDisplay))
            return _localizer.GetString("RecentJobs.Tooltip.JobId", job.ShortJobId);

        return $"{job.OrderDisplay}{Environment.NewLine}{_localizer.GetString("RecentJobs.Tooltip.JobId", job.ShortJobId)}";
    }

    private void OnRecentJobsCellPainting(object? sender, DataGridViewCellPaintingEventArgs e) =>
        PaintStatusBadgeCell(sender, e, _jobsForGrid);

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void OnRecentJobsCellToolTipTextNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || sender is not DataGridView grid || e.RowIndex >= _jobsForGrid.Count)
            return;

        var columnName = grid.Columns[e.ColumnIndex].Name;
        var job = _jobsForGrid[e.RowIndex];

        if (columnName == "Time")
        {
            e.ToolTipText = FormatTimeTooltip(job.DisplayTimeUtc);
            return;
        }

        if (columnName == "Order")
        {
            e.ToolTipText = FormatJobOrderTooltip(job);
            return;
        }

        if (columnName == "Status" &&
            job.Status == LocalPrintJobStatus.Failed &&
            !string.IsNullOrWhiteSpace(job.ErrorMessage))
        {
            e.ToolTipText = job.ErrorMessage;
            return;
        }

        var cellValue = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();
        if (!string.IsNullOrWhiteSpace(cellValue) && cellValue != _localizer["Common.Dash"])
            e.ToolTipText = cellValue;
    }

    private string FormatUtc(DateTime? value, string dash) =>
        value.HasValue
            ? PrintBridgeDateTimeFormatter.FormatDashboardUtc(_cultureService.CurrentCulture, value.Value)
            : dash;

    private string FormatTimeTooltip(DateTime utc) =>
        PrintBridgeDateTimeFormatter.FormatTooltipUtc(_cultureService.CurrentCulture, utc);

    private string GetUserErrorMessage(Exception ex)
    {
        if (ex is PrintBridgeConnectionException connectionEx)
            return _localizer.GetString(connectionEx.UserMessageKey, connectionEx.FormatArgs);

        if (ex is LocalizedApplicationException localized)
        {
            if (localized.ResourceKey.StartsWith("PrintBridge.Reprint", StringComparison.Ordinal)
                || localized.ResourceKey.StartsWith("Reprint.", StringComparison.Ordinal))
                return _localizer.GetReprintMessage(localized.ResourceKey);

            return _localizer.GetString(localized.ResourceKey, localized.Args);
        }

        return ex.Message;
    }

    public void PersistWindowLayout() =>
        PrintBridgeWindowLayout.Persist(this, _settingsStore, _settingsHolder);

    private void UpdateDryRunWarning()
    {
        if (_lblDryRunWarning == null || _chkDryRun == null)
            return;

        _lblDryRunWarning.Visible = _chkDryRun.Checked;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        PersistWindowLayout();
        _dashboardTimer.Stop();
        _dashboardTimer.Dispose();
        _jobsRefreshTimer.Stop();
        _jobsRefreshTimer.Dispose();
        DisposePrintHistoryUi();
        base.OnFormClosed(e);
    }
}
