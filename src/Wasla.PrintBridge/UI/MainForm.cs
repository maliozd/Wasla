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
    private Button _btnToggleToken = null!;
    private ComboBox _cmbPrinterName = null!;
    private Button _btnRefreshPrinters = null!;
    private CheckBox _chkDryRun = null!;
    private NumericUpDown _numIdlePoll = null!;
    private NumericUpDown _numBusyPoll = null!;
    private NumericUpDown _numErrorPoll = null!;
    private Button _btnSaveConnection = null!;
    private Button _btnTestSettingsConnection = null!;
    private Button _btnResetConnection = null!;
    private Label _lblResetConnectionHelp = null!;
    private Label _lblConnectionTroubleshootingTitle = null!;
    private Label _lblStartReconnectHint = null!;
    private Button _btnSavePrinter = null!;
    private Button _btnTestSettingsPrinter = null!;
    private Button _btnApplyLanguage = null!;
    private Button _btnSaveAdvanced = null!;
    private ComboBox _cmbLanguage = null!;
    private Label _lblConnectionSectionText = null!;
    private Label _lblPrinterSectionText = null!;
    private Label _lblLanguageSectionText = null!;
    private Panel _connectionGroup = null!;
    private Panel _printerGroup = null!;
    private Panel _languageGroup = null!;
    private Panel _advancedGroup = null!;
    private TableLayoutPanel _connectionFieldTable = null!;
    private TableLayoutPanel _printerFieldTable = null!;
    private TableLayoutPanel _languageFieldTable = null!;
    private TableLayoutPanel _advancedFieldTable = null!;
    private Label _lblConnectionSectionTitle = null!;
    private Label _lblPrinterSectionTitle = null!;
    private Label _lblLanguageSectionTitle = null!;
    private Label _lblAdvancedSectionTitle = null!;
    private Label _lblConnectionStatus = null!;
    private Label _lblPrinterStatus = null!;
    private Label _lblLanguageStatus = null!;
    private Label _lblAdvancedStatus = null!;
    private Label _lblServerUrl = null!;
    private Label _lblAgentToken = null!;
    private Label _lblServerUrlHelp = null!;
    private Label _lblAgentTokenHelp = null!;
    private Label _lblAgentTokenPasteGuard = null!;
    private Label _lblPrinterName = null!;
    private Label _lblLanguage = null!;
    private Label _lblDryRunMode = null!;
    private Label _lblDryRunWarning = null!;
    private Label _lblIdlePoll = null!;
    private Label _lblBusyPoll = null!;
    private Label _lblErrorPoll = null!;
    private Label _settingsHint = null!;
    private bool _suppressLanguageSelectionChanged;
    private bool _isSavingLanguage;
    private bool _agentTokenUserEdited;
    private bool _syncingConnectionFields;

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
        Font = PrintBridgeUiTheme.BodyFont;
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = PrintBridgeUiTheme.PageBackground;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = PrintBridgeUiTheme.BodyFont,
            Padding = new Point(6, 4)
        };
        _statusTab = new TabPage { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(8) };
        _logsTab = new TabPage { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(8) };
        _historyTab = new TabPage { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(8) };
        _settingsTab = new TabPage { BackColor = PrintBridgeUiTheme.PageBackground, Padding = new Padding(8) };
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
        _cultureService.CultureChanged += OnCultureChangedElsewhere;
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
        _btnResetConnection.Text = _localizer["Button.ResetConnection"];
        PrintBridgeSettingsLayout.ApplyTroubleshootingActionButtonLayout(_btnResetConnection);
        _lblConnectionTroubleshootingTitle.Text = _localizer["Settings.ConnectionTroubleshootingTitle"];
        _lblResetConnectionHelp.Text = _localizer["Settings.ResetConnectionHelp"];
        _btnSavePrinter.Text = _localizer["Button.SavePrinter"];
        _btnTestSettingsPrinter.Text = _localizer["Button.TestPrinter"];
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
        RefreshAgentTokenPasteGuard();
        _lblPrinterName.Text = _localizer["Settings.PrinterName"];
        _lblLanguage.Text = _localizer["Settings.Language"];
        _lblConnectionSectionTitle.Text = _localizer["Settings.Section.Connection"];
        _lblPrinterSectionTitle.Text = _localizer["Settings.Section.Printer"];
        _lblLanguageSectionTitle.Text = _localizer["Settings.Section.Language"];
        _lblAdvancedSectionTitle.Text = _localizer["Settings.Advanced"];
        _lblConnectionSectionText.Text = _localizer["Settings.Section.ConnectionHelp"];
        _lblPrinterSectionText.Text = _localizer["Settings.Section.PrinterHelp"];
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

        var isRtl = _cultureService.IsRightToLeft;
        _footerDeviceLabel.TextAlign = isRtl ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
        _footerVersionLabel.TextAlign = isRtl ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight;
        PrintBridgeRtl.Apply(this, isRtl);
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
            Padding = new Padding(8, 4, 8, 4),
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

        var footerFont = PrintBridgeUiTheme.FooterFont;
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
            Margin = new Padding(0, 0, 0, 6)
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
            Margin = new Padding(0, 0, 0, 1)
        };
        _subtitleLabel = new Label
        {
            Font = PrintBridgeUiTheme.SubtitleFont,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 4)
        };
        _headerBadge = new Label
        {
            Font = PrintBridgeUiTheme.BadgeFont,
            ForeColor = Color.White,
            BackColor = PrintBridgeUiTheme.Inactive,
            AutoSize = true,
            Padding = new Padding(8, 2, 8, 2),
            Dock = DockStyle.Left,
            Margin = new Padding(0, 0, 0, 0)
        };
        header.Controls.Add(_titleLabel, 0, 0);
        header.Controls.Add(_subtitleLabel, 0, 1);
        header.Controls.Add(_headerBadge, 0, 2);
        root.Controls.Add(header, 0, 0);

        var metricsHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoScroll = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        var metricsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0)
        };

        var serverStatusCard = PrintBridgeUiTheme.CreateMetricCard(
            string.Empty,
            out _serverStatusValue,
            out _serverStatusTitle,
            PrintBridgeUiTheme.MetricStatusFont);
        metricsFlow.Controls.Add(serverStatusCard);
        metricsFlow.Controls.Add(PrintBridgeUiTheme.CreateMetricCard(
            string.Empty,
            out _printerStatusValue,
            out _printerStatusTitle,
            PrintBridgeUiTheme.MetricStatusFont));

        var lastContactCard = PrintBridgeUiTheme.CreateMetricCard(
            string.Empty,
            out _lastContactValue,
            out _lastContactTitle,
            PrintBridgeUiTheme.MetricTimestampFont);
        metricsFlow.Controls.Add(lastContactCard);
        metricsFlow.Controls.Add(PrintBridgeUiTheme.CreateMetricCard(string.Empty, out _jobsTodayValue, out _jobsTodayTitle));
        metricsFlow.Controls.Add(PrintBridgeUiTheme.CreateMetricCard(string.Empty, out _failedTodayValue, out _failedTodayTitle));

        var lastPrintCard = PrintBridgeUiTheme.CreateMetricCard(
            string.Empty,
            out _lastPrintValue,
            out _lastPrintTitle,
            PrintBridgeUiTheme.MetricTimestampFont);
        metricsFlow.Controls.Add(lastPrintCard);
        metricsHost.Controls.Add(metricsFlow);
        root.Controls.Add(metricsHost, 0, 1);

        _jobsGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Font = PrintBridgeUiTheme.SectionFont,
            ForeColor = PrintBridgeUiTheme.TextTitle,
            Padding = new Padding(8, 16, 8, 8),
            Margin = new Padding(0, 0, 0, 6)
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
            Font = PrintBridgeUiTheme.SectionFont
        };
        jobsPanel.Controls.Add(_recentJobsGrid);
        jobsPanel.Controls.Add(_recentJobsEmptyLabel);
        _jobsGroup.Controls.Add(jobsPanel);
        root.Controls.Add(_jobsGroup, 0, 2);

        var actionBar = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 0)
        };
        var actionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2
        };
        actionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        actionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

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

        _lblStartReconnectHint = new Label
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Font = PrintBridgeUiTheme.HelperFont,
            Margin = new Padding(0, 6, 0, 0),
            MaximumSize = new Size(900, 0),
            Visible = false
        };

        actionLayout.Controls.Add(buttonPanel, 0, 0);
        actionLayout.Controls.Add(_lblStartReconnectHint, 0, 1);
        actionBar.Controls.Add(actionLayout);
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
        _settingsScrollPanel = new DoubleBufferedPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        // Fully static layout: cards stretch with the window (Dock=Top), but every
        // form control inside has a fixed compact width, so nothing is recalculated
        // while resizing and no Resize/Layout handlers are needed.
        _settingsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0),
            ColumnCount = 1,
            RowCount = 0
        };
        _settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _connectionFieldTable = PrintBridgeSettingsLayout.CreateFieldTable(7);
        _lblConnectionStatus = PrintBridgeSettingsLayout.CreateSectionStatusLabel();
        AddServerUrlRow(_connectionFieldTable);
        AddTokenRow(_connectionFieldTable);
        _btnSaveConnection = PrintBridgeUiTheme.CreateActionButton(string.Empty, primary: true);
        _btnSaveConnection.Click += async (_, _) => await SaveConnectionSettingsAsync().ConfigureAwait(true);
        _btnTestSettingsConnection = PrintBridgeUiTheme.CreateActionButton(string.Empty);
        _btnTestSettingsConnection.Click += async (_, _) => await TestConnectionFromSettingsAsync().ConfigureAwait(true);
        AddSectionActions(_connectionFieldTable, 5, _btnSaveConnection, _btnTestSettingsConnection);
        _connectionFieldTable.Controls.Add(_lblConnectionStatus, 1, 6);

        var troubleshootingRow = PrintBridgeSettingsLayout.CreateTroubleshootingRow(
            out _lblConnectionTroubleshootingTitle,
            out _lblResetConnectionHelp,
            out _btnResetConnection);
        _btnResetConnection.Click += async (_, _) => await ResetConnectionAsync().ConfigureAwait(true);

        // Keep troubleshooting outside the field table so the reset button
        // stays stacked below connection actions.
        var connectionContent = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Dock = DockStyle.Top,
            Margin = new Padding(0)
        };
        connectionContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        connectionContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        connectionContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        connectionContent.Controls.Add(_connectionFieldTable, 0, 0);
        connectionContent.Controls.Add(troubleshootingRow, 0, 1);

        _connectionGroup = PrintBridgeSettingsLayout.CreateSectionCard(
            out _lblConnectionSectionTitle,
            out _lblConnectionSectionText,
            connectionContent);
        AddSettingsSection(_settingsLayout, _connectionGroup);

        _printerFieldTable = PrintBridgeSettingsLayout.CreateFieldTable(3);
        _lblPrinterStatus = PrintBridgeSettingsLayout.CreateSectionStatusLabel();
        AddPrinterRow(_printerFieldTable);
        _btnSavePrinter = PrintBridgeUiTheme.CreateActionButton(string.Empty, primary: true);
        _btnSavePrinter.Click += async (_, _) => await SavePrinterSettingsAsync().ConfigureAwait(true);
        _btnTestSettingsPrinter = PrintBridgeUiTheme.CreateActionButton(string.Empty);
        _btnTestSettingsPrinter.Click += async (_, _) => await TestPrinterFromSettingsAsync().ConfigureAwait(true);
        AddSectionActions(_printerFieldTable, 1, _btnSavePrinter, _btnTestSettingsPrinter);
        _printerFieldTable.Controls.Add(_lblPrinterStatus, 1, 2);
        _printerGroup = PrintBridgeSettingsLayout.CreateSectionCard(
            out _lblPrinterSectionTitle,
            out _lblPrinterSectionText,
            _printerFieldTable);
        AddSettingsSection(_settingsLayout, _printerGroup);

        _languageFieldTable = PrintBridgeSettingsLayout.CreateFieldTable(3);
        _lblLanguageStatus = PrintBridgeSettingsLayout.CreateSectionStatusLabel();
        AddLanguageRow(_languageFieldTable);
        _btnApplyLanguage = PrintBridgeUiTheme.CreateActionButton(string.Empty, primary: true);
        _btnApplyLanguage.Click += async (_, _) => await SaveSelectedLanguageAsync().ConfigureAwait(true);
        AddSectionActions(_languageFieldTable, 1, _btnApplyLanguage);
        _languageFieldTable.Controls.Add(_lblLanguageStatus, 1, 2);
        _languageGroup = PrintBridgeSettingsLayout.CreateSectionCard(
            out _lblLanguageSectionTitle,
            out _lblLanguageSectionText,
            _languageFieldTable);
        AddSettingsSection(_settingsLayout, _languageGroup);

        _advancedFieldTable = PrintBridgeSettingsLayout.CreateFieldTable(6);
        _lblAdvancedStatus = PrintBridgeSettingsLayout.CreateSectionStatusLabel();

        _lblDryRunMode = PrintBridgeSettingsLayout.CreateFieldLabel();
        _advancedFieldTable.Controls.Add(_lblDryRunMode, 0, 0);

        var dryRunPanel = new TableLayoutPanel
        {
            Dock = DockStyle.None,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
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
        _advancedFieldTable.Controls.Add(dryRunPanel, 1, 0);

        (_numIdlePoll, _lblIdlePoll) = AddSettingsNumericRow(_advancedFieldTable, 1, 1, 300, 5);
        (_numBusyPoll, _lblBusyPoll) = AddSettingsNumericRow(_advancedFieldTable, 2, 1, 60, 1);
        (_numErrorPoll, _lblErrorPoll) = AddSettingsNumericRow(_advancedFieldTable, 3, 1, 300, 15);
        _btnSaveAdvanced = PrintBridgeUiTheme.CreateActionButton(string.Empty, primary: true);
        _btnSaveAdvanced.Click += async (_, _) => await SaveAdvancedSettingsAsync().ConfigureAwait(true);
        AddSectionActions(_advancedFieldTable, 4, _btnSaveAdvanced);
        _advancedFieldTable.Controls.Add(_lblAdvancedStatus, 1, 5);
        _advancedGroup = PrintBridgeSettingsLayout.CreateSectionCard(
            out _lblAdvancedSectionTitle,
            out var advancedDescription,
            _advancedFieldTable);
        advancedDescription.Visible = false;
        advancedDescription.Margin = Padding.Empty;
        AddSettingsSection(_settingsLayout, _advancedGroup);
        InitializeSettingsSaveUi();

        _settingsHint = new Label
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Dock = DockStyle.Bottom,
            Padding = new Padding(4, 8, 4, 4)
        };

        var settingsHost = new DoubleBufferedPanel { Dock = DockStyle.Fill };
        _settingsScrollPanel.Controls.Add(_settingsLayout);
        settingsHost.Controls.Add(_settingsScrollPanel);
        settingsHost.Controls.Add(_settingsHint);
        _settingsTab.Controls.Add(settingsHost);

        WireConnectionSettingsChangeHandlers();
    }

    private static void AddSettingsSection(TableLayoutPanel root, Control section)
    {
        var row = root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.Dock = DockStyle.Top;
        section.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        root.Controls.Add(section, 0, row);
    }

    private static void AddSectionActions(TableLayoutPanel table, int row, params Button[] buttons)
    {
        var panel = PrintBridgeSettingsLayout.CreateActionRow(buttons);
        table.Controls.Add(panel, 1, row);
    }

    private void AddLanguageRow(TableLayoutPanel table)
    {
        PrintBridgeSettingsLayout.ConfigureInputRow(table, 0);

        _lblLanguage = PrintBridgeSettingsLayout.CreateFieldLabel();
        table.Controls.Add(_lblLanguage, 0, 0);

        _cmbLanguage = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        PrintBridgeUiTheme.StyleSettingsComboBox(_cmbLanguage);
        PrintBridgeSettingsLayout.StyleSingleLineInput(
            _cmbLanguage,
            PrintBridgeSettingsLayout.LanguageComboWidth);
        _cmbLanguage.SelectedIndexChanged += (_, _) => SetSectionStatus(_lblLanguageStatus, null);
        table.Controls.Add(PrintBridgeSettingsLayout.CreateInputBody(_cmbLanguage), 1, 0);
    }

    private void AddServerUrlRow(TableLayoutPanel table)
    {
        PrintBridgeSettingsLayout.ConfigureInputRow(table, 0);

        _lblServerUrl = PrintBridgeSettingsLayout.CreateFieldLabel();
        table.Controls.Add(_lblServerUrl, 0, 0);

        _txtServerUrl = new TextBox();
        PrintBridgeUiTheme.StyleSettingsTextBox(_txtServerUrl);
        PrintBridgeSettingsLayout.StyleSingleLineInput(_txtServerUrl, PrintBridgeSettingsLayout.UrlInputWidth);
        table.Controls.Add(PrintBridgeSettingsLayout.CreateInputBody(_txtServerUrl), 1, 0);

        _lblServerUrlHelp = PrintBridgeSettingsLayout.CreateHelpLabel();
        table.Controls.Add(_lblServerUrlHelp, 1, 1);
    }

    private void AddTokenRow(TableLayoutPanel table)
    {
        PrintBridgeSettingsLayout.ConfigureInputRow(table, 2);

        _lblAgentToken = PrintBridgeSettingsLayout.CreateFieldLabel();
        table.Controls.Add(_lblAgentToken, 0, 2);

        _txtAgentToken = new TextBox
        {
            UseSystemPasswordChar = true
        };
        PrintBridgeUiTheme.StyleSettingsTextBox(_txtAgentToken);
        PrintBridgeSettingsLayout.StyleSingleLineInput(_txtAgentToken, PrintBridgeSettingsLayout.TokenInputWidth);

        _btnToggleToken = new Button();
        PrintBridgeSettingsLayout.StyleSideActionButton(_btnToggleToken);
        _btnToggleToken.Click += (_, _) =>
        {
            _txtAgentToken.UseSystemPasswordChar = !_txtAgentToken.UseSystemPasswordChar;
            _btnToggleToken.Text = _txtAgentToken.UseSystemPasswordChar
                ? _localizer["Button.ShowToken"]
                : _localizer["Button.HideToken"];
        };
        table.Controls.Add(PrintBridgeSettingsLayout.CreateInputBody(_txtAgentToken, _btnToggleToken), 1, 2);

        _lblAgentTokenHelp = PrintBridgeSettingsLayout.CreateHelpLabel();
        table.Controls.Add(_lblAgentTokenHelp, 1, 3);

        _lblAgentTokenPasteGuard = PrintBridgeSettingsLayout.CreateFieldInfoLabel();
        table.Controls.Add(_lblAgentTokenPasteGuard, 1, 4);
    }

    private void AddPrinterRow(TableLayoutPanel table)
    {
        PrintBridgeSettingsLayout.ConfigureInputRow(table, 0);

        _lblPrinterName = PrintBridgeSettingsLayout.CreateFieldLabel();
        table.Controls.Add(_lblPrinterName, 0, 0);

        _cmbPrinterName = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown
        };
        PrintBridgeUiTheme.StyleSettingsComboBox(_cmbPrinterName);
        PrintBridgeSettingsLayout.StyleSingleLineInput(_cmbPrinterName, PrintBridgeSettingsLayout.PrinterComboWidth);

        _btnRefreshPrinters = new Button();
        PrintBridgeSettingsLayout.StyleSideActionButton(_btnRefreshPrinters);
        _btnRefreshPrinters.Click += (_, _) => RefreshPrinterList();
        table.Controls.Add(PrintBridgeSettingsLayout.CreateInputBody(_cmbPrinterName, _btnRefreshPrinters), 1, 0);
    }

    private void OnRuntimeStatusChanged(object? sender, EventArgs e)
    {
        QueueRefreshDashboard();
        QueueUiAction(SyncConnectionFieldsFromHolder);
        QueueUiAction(() => RefreshSettingsConnectionStatus());
        QueueRefreshRecentJobs();
        QueueUiAction(() =>
        {
            if (_tabs.SelectedTab == _historyTab)
                RefreshPrintHistory();
        });
    }

    private void OnCultureChangedElsewhere(object? sender, EventArgs e)
    {
        // A change made in this window is applied by SaveSelectedLanguageAsync itself.
        if (_isSavingLanguage)
            return;

        QueueUiAction(() =>
        {
            ApplyStartupLocalization();
            SelectSavedLanguage();
            RefreshDashboard();
            RefreshRecentJobsFromRuntime();
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
        QueueUiAction(() => _ = CompleteAutomaticSetupRecoveryAsync());
    }

    private async Task CompleteAutomaticSetupRecoveryAsync()
    {
        try
        {
            LoadSettingsIntoForm();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _runtime.VerifyAndResumeAsync(cts.Token).ConfigureAwait(true);

            RefreshDashboard();
            RefreshRecentJobsFromRuntime();
        }
        catch (Exception ex)
        {
            _settingsLogger?.LogWarning(ex, "Automatic setup settings refresh could not update the open window.");
        }
    }

    /// <summary>
    /// The trusted native connection setup used by the WebView2 shell's reconnect action. The server URL and
    /// device token are only ever entered here, never in the WebView2 page.
    /// </summary>
    public void FocusConnectionSettingsSection()
    {
        SelectSettingsTab();
        _connectionGroup.Focus();
        _txtServerUrl.Focus();
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

    private static (NumericUpDown Numeric, Label Label) AddSettingsNumericRow(
        TableLayoutPanel table,
        int row,
        decimal min,
        decimal max,
        decimal value)
    {
        var label = PrintBridgeSettingsLayout.CreateFieldLabel();
        table.Controls.Add(label, 0, row);
        var numeric = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 110,
            Anchor = AnchorStyles.Left,
            Font = PrintBridgeUiTheme.BodyFont,
            Margin = new Padding(0, 4, 0, 0)
        };
        table.Controls.Add(numeric, 1, row);
        return (numeric, label);
    }

    private void PopulateLanguageCombo()
    {
        _cmbLanguage.Items.Clear();
        foreach (var culture in SupportedCultures.All)
            _cmbLanguage.Items.Add(new LanguageOption(culture, SupportedCultures.GetNativeName(culture)));
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
            RefreshRuntimeIssueLabel();
            RefreshSettingsConnectionStatus();
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

    private void SyncConnectionFieldsFromHolder()
    {
        if (_txtServerUrl is null || _txtAgentToken is null)
            return;

        var (hub, _, _) = _settingsHolder.Snapshot();
        _syncingConnectionFields = true;
        try
        {
            _txtServerUrl.Text = hub.ServerUrl ?? string.Empty;
            if (!ShouldPreserveAgentTokenInput())
                _txtAgentToken.Text = hub.AgentToken ?? string.Empty;
        }
        finally
        {
            _syncingConnectionFields = false;
        }

        RefreshAgentTokenPasteGuard();
    }

    private bool ShouldPreserveAgentTokenInput() =>
        _txtAgentToken is not null
        && PrintBridgeConnectionFieldSync.ShouldPreserveAgentTokenInput(
            _isSavingConnection,
            _txtAgentToken.Focused,
            _agentTokenUserEdited);

    private void ResetAgentTokenInputTracking()
    {
        _agentTokenUserEdited = false;
    }

    private void RefreshAgentTokenPasteGuard()
    {
        if (_lblAgentTokenPasteGuard is null || _txtAgentToken is null)
            return;

        // Static AutoSize layout: showing/hiding the reserved paste-guard row
        // reflows automatically; no manual scroll-layout pass is needed.
        var looksLikeSetupOrProtocol = PrintBridgeTokenPasteGuard.LooksLikeSetupOrProtocolValue(_txtAgentToken.Text);
        if (looksLikeSetupOrProtocol)
        {
            _lblAgentTokenPasteGuard.Text = _localizer["Settings.AgentTokenPasteGuard"];
            _lblAgentTokenPasteGuard.Visible = true;
        }
        else
        {
            _lblAgentTokenPasteGuard.Text = string.Empty;
            _lblAgentTokenPasteGuard.Visible = false;
        }
    }

    private void RefreshSettingsConnectionStatus(PrintBridgeRuntimeStatus? status = null)
    {
        if (_lblConnectionStatus is null || _isSavingConnection)
            return;

        status ??= _runtime.GetStatus();
        if (status.LastIssue is not { IsBlockingLifecycleIssue: true } issue)
        {
            SetSectionStatus(_lblConnectionStatus, null);
            return;
        }

        SetSectionStatus(_lblConnectionStatus, _localizer.GetRuntimeIssueDetail(issue), isError: true);
        if (issue.ShouldClearToken && !ShouldPreserveAgentTokenInput())
            SyncConnectionFieldsFromHolder();
    }

    private void LoadSettingsIntoForm()
    {
        ResetAgentTokenInputTracking();
        var (hub, bridge, ui) = _settingsHolder.Snapshot();
        _syncingConnectionFields = true;
        try
        {
            _txtServerUrl.Text = hub.ServerUrl;
            _txtAgentToken.Text = hub.AgentToken;
            RefreshPrinterList(bridge.PrinterName);
            _chkDryRun.Checked = bridge.DryRun;
            UpdateDryRunWarning();
            _numIdlePoll.Value = Math.Clamp(bridge.IdlePollIntervalSeconds, (int)_numIdlePoll.Minimum, (int)_numIdlePoll.Maximum);
            _numBusyPoll.Value = Math.Clamp(bridge.BusyPollIntervalSeconds, (int)_numBusyPoll.Minimum, (int)_numBusyPoll.Maximum);
            _numErrorPoll.Value = Math.Clamp(bridge.ErrorPollIntervalSeconds, (int)_numErrorPoll.Minimum, (int)_numErrorPoll.Maximum);
            SelectSavedLanguage();
        }
        finally
        {
            _syncingConnectionFields = false;
        }
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

        var hub = _settingsHolder.Snapshot().OrderHub;
        var status = _runtime.GetStatus();
        if (PrintBridgePollingGate.RequiresReconnectBeforeStart(hub.AgentToken, status.LastIssue))
            return;

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

        var serverStatusText = _localizer.GetServerConnectionStatus(status);
        _serverStatusValue.Text = serverStatusText;
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
        RefreshSettingsConnectionStatus(status);
    }

    private void RefreshRuntimeIssueLabel(PrintBridgeRuntimeStatus? status = null)
    {
        RefreshSettingsConnectionStatus(status);
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
        if (status.LastIssue is { IsBlockingLifecycleIssue: true })
            _headerBadge.BackColor = PrintBridgeUiTheme.Danger;
        else if (!status.IsRunning && status.IsConnected)
            _headerBadge.BackColor = PrintBridgeUiTheme.Success;
        else if (status.IsRunning)
            _headerBadge.BackColor = PrintBridgeUiTheme.Info;
        else
            _headerBadge.BackColor = PrintBridgeUiTheme.Inactive;
    }

    private void UpdateStartStopButton(PrintBridgeRuntimeStatus status)
    {
        var hub = _settingsHolder.Snapshot().OrderHub;
        var requiresReconnect = PrintBridgePollingGate.RequiresReconnectBeforeStart(hub.AgentToken, status.LastIssue);

        _btnStartStop.Text = status.IsRunning
            ? _localizer["Button.StopListening"]
            : _localizer["Button.StartListening"];
        _btnStartStop.BackColor = status.IsRunning ? PrintBridgeUiTheme.Danger : PrintBridgeUiTheme.PrimaryButton;
        _btnStartStop.FlatAppearance.MouseOverBackColor = status.IsRunning
            ? Color.FromArgb(200, 45, 60)
            : PrintBridgeUiTheme.PrimaryButtonHover;

        if (status.IsRunning)
        {
            _btnStartStop.Enabled = true;
            _lblStartReconnectHint.Visible = false;
            _lblStartReconnectHint.Text = string.Empty;
            return;
        }

        _btnStartStop.Enabled = !requiresReconnect;
        _lblStartReconnectHint.Visible = requiresReconnect;
        _lblStartReconnectHint.Text = requiresReconnect
            ? _localizer["Message.ReconnectFromWebToContinue"]
            : string.Empty;
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
            return _localizer.GetRuntimeIssueDetail(new(
                connectionEx.IssueCode,
                connectionEx.UserMessageKey,
                connectionEx.FormatArgs));

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
        _cultureService.CultureChanged -= OnCultureChangedElsewhere;
        _dashboardTimer.Stop();
        _dashboardTimer.Dispose();
        _jobsRefreshTimer.Stop();
        _jobsRefreshTimer.Dispose();
        DisposePrintHistoryUi();
        base.OnFormClosed(e);
    }
}
