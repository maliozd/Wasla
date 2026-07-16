using System.Drawing.Drawing2D;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;

namespace Wasla.PrintBridge.UI;

public sealed partial class MainForm
{
    private const int HistorySearchDebounceMs = 300;

    private DataGridView _historyGrid = null!;
    private Label _historyEmptyLabel = null!;
    private Label _lblHistorySearch = null!;
    private ComboBox _historyFilterCombo = null!;
    private TextBox _historySearchBox = null!;
    private Button _btnReprint = null!;
    private System.Windows.Forms.Timer? _historySearchDebounceTimer;
    private IReadOnlyList<LocalPrintJobRecord> _historyJobsForGrid = Array.Empty<LocalPrintJobRecord>();

    private void BuildHistoryTab()
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
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 8)
        };

        _historyFilterCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 160,
            Height = PrintBridgeUiTheme.ToolbarControlHeight,
            Margin = new Padding(0, 0, 6, 0)
        };
        _historyFilterCombo.Items.Add(_localizer["PrintHistory.Filter.Today"]);
        _historyFilterCombo.Items.Add(_localizer["PrintHistory.Filter.Last7Days"]);
        _historyFilterCombo.Items.Add(_localizer["PrintHistory.Filter.Last30Days"]);
        _historyFilterCombo.SelectedIndex = 0;
        _historyFilterCombo.SelectedIndexChanged += (_, _) => RefreshPrintHistory();
        toolbar.Controls.Add(_historyFilterCombo);

        _lblHistorySearch = new Label
        {
            AutoSize = true,
            Margin = new Padding(6, 5, 4, 0),
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Font = PrintBridgeUiTheme.BodyFont
        };
        toolbar.Controls.Add(_lblHistorySearch);

        _historySearchBox = new TextBox
        {
            Width = 200,
            Height = PrintBridgeUiTheme.ToolbarControlHeight,
            Margin = new Padding(0, 0, 6, 0),
            Font = PrintBridgeUiTheme.BodyFont
        };
        _historySearchDebounceTimer = new System.Windows.Forms.Timer { Interval = HistorySearchDebounceMs };
        _historySearchDebounceTimer.Tick += (_, _) =>
        {
            _historySearchDebounceTimer.Stop();
            RefreshPrintHistory();
        };
        _historySearchBox.TextChanged += (_, _) =>
        {
            _historySearchDebounceTimer?.Stop();
            _historySearchDebounceTimer?.Start();
        };
        toolbar.Controls.Add(_historySearchBox);

        _btnReprint = PrintBridgeUiTheme.CreateToolbarPrimaryButton(string.Empty);
        _btnReprint.Enabled = false;
        _btnReprint.Click += async (_, _) => await RunSafeAsync(ReprintSelectedHistoryJobAsync);
        toolbar.Controls.Add(_btnReprint);

        var gridPanel = new Panel { Dock = DockStyle.Fill, MinimumSize = new Size(0, 320) };
        _historyGrid = new DataGridView
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
        PrintBridgeUiTheme.StyleGrid(_historyGrid);
        _historyGrid.Columns.Add("Time", string.Empty);
        _historyGrid.Columns.Add("Order", string.Empty);
        _historyGrid.Columns.Add("Platform", string.Empty);
        _historyGrid.Columns.Add("Printer", string.Empty);
        _historyGrid.Columns.Add("Status", string.Empty);
        _historyGrid.Columns["Time"]!.FillWeight = 20;
        _historyGrid.Columns["Time"]!.MinimumWidth = 128;
        _historyGrid.Columns["Order"]!.FillWeight = 28;
        _historyGrid.Columns["Order"]!.MinimumWidth = 140;
        _historyGrid.Columns["Platform"]!.FillWeight = 16;
        _historyGrid.Columns["Platform"]!.MinimumWidth = 100;
        _historyGrid.Columns["Printer"]!.FillWeight = 20;
        _historyGrid.Columns["Printer"]!.MinimumWidth = 100;
        _historyGrid.Columns["Status"]!.FillWeight = 14;
        _historyGrid.Columns["Status"]!.MinimumWidth = 112;
        _historyGrid.SelectionChanged += (_, _) => UpdateReprintButtonState();
        _historyGrid.CellPainting += OnHistoryGridCellPainting;
        _historyGrid.CellToolTipTextNeeded += OnHistoryGridCellToolTipTextNeeded;
        _historyGrid.ShowCellToolTips = true;

        _historyEmptyLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Font = PrintBridgeUiTheme.SectionFont
        };

        gridPanel.Controls.Add(_historyGrid);
        gridPanel.Controls.Add(_historyEmptyLabel);
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(gridPanel, 0, 1);
        _historyTab.Controls.Add(root);
    }

    private void ApplyPrintHistoryLocalization()
    {
        _btnReprint.Text = _localizer["Reprint.Button"];
        PrintBridgeUiTheme.ApplyToolbarPrimaryButtonWidth(_btnReprint);
        PrintBridgeUiTheme.ApplyToolbarPrimaryButtonAppearance(_btnReprint);
        _lblHistorySearch.Text = _localizer["PrintHistory.Search"];
        _historySearchBox.PlaceholderText = _localizer["PrintHistory.SearchPlaceholder"];
        _historyGrid.Columns["Time"]!.HeaderText = _localizer["PrintHistory.Column.Time"];
        _historyGrid.Columns["Order"]!.HeaderText = _localizer["PrintHistory.Column.Order"];
        _historyGrid.Columns["Platform"]!.HeaderText = _localizer["PrintHistory.Column.Platform"];
        _historyGrid.Columns["Printer"]!.HeaderText = _localizer["PrintHistory.Column.Printer"];
        _historyGrid.Columns["Status"]!.HeaderText = _localizer["PrintHistory.Column.Status"];

        var selectedIndex = Math.Clamp(_historyFilterCombo.SelectedIndex, 0, 2);
        _historyFilterCombo.Items.Clear();
        _historyFilterCombo.Items.Add(_localizer["PrintHistory.Filter.Today"]);
        _historyFilterCombo.Items.Add(_localizer["PrintHistory.Filter.Last7Days"]);
        _historyFilterCombo.Items.Add(_localizer["PrintHistory.Filter.Last30Days"]);
        _historyFilterCombo.SelectedIndex = selectedIndex;

        RefreshPrintHistoryEmptyLabel();
    }

    private PrintHistoryDateFilter GetSelectedHistoryFilter() =>
        _historyFilterCombo.SelectedIndex switch
        {
            1 => PrintHistoryDateFilter.Last7Days,
            2 => PrintHistoryDateFilter.Last30Days,
            _ => PrintHistoryDateFilter.Today
        };

    private void RefreshPrintHistory()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshPrintHistory);
            return;
        }

        if (_historyFilterCombo is null)
            return;

        var filter = GetSelectedHistoryFilter();
        var search = _historySearchBox.Text;
        var jobs = _runtime.GetPrintHistory(filter, search);
        _historyJobsForGrid = jobs;

        RefreshPrintHistoryEmptyLabel(search);

        _historyEmptyLabel.Visible = jobs.Count == 0;
        _historyGrid.Visible = jobs.Count > 0;

        var scrollIndex = _historyGrid.FirstDisplayedScrollingRowIndex;
        _historyGrid.SuspendLayout();
        try
        {
            _historyGrid.Rows.Clear();
            foreach (var job in jobs)
            {
                _historyGrid.Rows.Add(
                    FormatUtc(job.DisplayTimeUtc, _localizer["Common.Dash"]),
                    FormatJobLabel(job),
                    _localizer.GetPlatform(job.Platform),
                    string.IsNullOrWhiteSpace(job.PrinterName) ? _localizer["Common.Dash"] : job.PrinterName,
                    _localizer.GetJobStatusBadge(job.Status));
            }
        }
        finally
        {
            _historyGrid.ResumeLayout(performLayout: false);
            _historyGrid.PerformLayout();
        }

        if (scrollIndex >= 0 && scrollIndex < _historyGrid.RowCount)
            _historyGrid.FirstDisplayedScrollingRowIndex = scrollIndex;

        UpdateReprintButtonState();
    }

    private void RefreshPrintHistoryEmptyLabel(string? search = null)
    {
        if (_historyEmptyLabel is null)
            return;

        search ??= _historySearchBox?.Text;
        _historyEmptyLabel.Text = !string.IsNullOrWhiteSpace(search)
            ? _localizer["PrintHistory.EmptyFiltered"]
            : _localizer["PrintHistory.Empty"];
    }

    private void UpdateReprintButtonState()
    {
        if (_btnReprint is null || _historyGrid is null)
            return;

        var canReprint = false;
        if (_historyGrid.SelectedRows.Count == 1)
        {
            var index = _historyGrid.SelectedRows[0].Index;
            if (index >= 0 && index < _historyJobsForGrid.Count)
                canReprint = _historyJobsForGrid[index].Status == LocalPrintJobStatus.Printed;
        }

        _btnReprint.Enabled = canReprint;
        PrintBridgeUiTheme.ApplyToolbarPrimaryButtonAppearance(_btnReprint);
    }

    private async Task ReprintSelectedHistoryJobAsync()
    {
        if (_historyGrid.SelectedRows.Count != 1)
        {
            MessageBox.Show(
                _localizer["Reprint.SelectPrintedJob"],
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var index = _historyGrid.SelectedRows[0].Index;
        if (index < 0 || index >= _historyJobsForGrid.Count)
            return;

        var job = _historyJobsForGrid[index];
        if (job.Status != LocalPrintJobStatus.Printed)
        {
            MessageBox.Show(
                _localizer["Reprint.NotAllowed"],
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var orderLabel = FormatJobLabel(job);
        var confirm = MessageBox.Show(
            _localizer.GetString("Reprint.Confirm", orderLabel),
            PrintBridgePaths.ProductDisplayName,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
            return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await _runtime.ReprintJobAsync(job.JobId, cts.Token).ConfigureAwait(true);
        MessageBox.Show(
            _localizer.GetReprintMessage(result.MessageKey),
            PrintBridgePaths.ProductDisplayName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private string FormatHistoryJobTooltip(LocalPrintJobRecord job)
    {
        var timeUtc = job.PrintedAtUtc ?? job.DisplayTimeUtc;
        var timeLabel = FormatTimeTooltip(timeUtc);
        var printerLabel = string.IsNullOrWhiteSpace(job.PrinterName)
            ? _localizer["Common.Dash"]
            : job.PrinterName;

        return _localizer.GetPrintHistoryJobTooltip(
            job,
            FormatJobLabel(job),
            _localizer.GetPlatform(job.Platform),
            printerLabel,
            _localizer.GetJobStatus(job.Status),
            timeLabel);
    }

    private void OnHistoryGridCellPainting(object? sender, DataGridViewCellPaintingEventArgs e) =>
        PaintStatusBadgeCell(sender, e, _historyJobsForGrid);

    private void OnHistoryGridCellToolTipTextNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || sender is not DataGridView grid || e.RowIndex >= _historyJobsForGrid.Count)
            return;

        var job = _historyJobsForGrid[e.RowIndex];
        e.ToolTipText = FormatHistoryJobTooltip(job);
    }

    private void DisposePrintHistoryUi()
    {
        if (_historySearchDebounceTimer is null)
            return;

        _historySearchDebounceTimer.Stop();
        _historySearchDebounceTimer.Dispose();
        _historySearchDebounceTimer = null;
    }

    private void PaintStatusBadgeCell(
        object? sender,
        DataGridViewCellPaintingEventArgs e,
        IReadOnlyList<LocalPrintJobRecord> jobs)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || sender is not DataGridView grid || e.Graphics is null)
            return;

        if (grid.Columns[e.ColumnIndex].Name != "Status" || e.RowIndex >= jobs.Count)
            return;

        var job = jobs[e.RowIndex];
        var badgeText = _localizer.GetJobStatusBadge(job.Status);
        var (backColor, foreColor) = PrintBridgeUiTheme.GetStatusBadgeColors(job.Status);

        e.Paint(
            e.ClipBounds,
            DataGridViewPaintParts.Background |
            DataGridViewPaintParts.Border |
            DataGridViewPaintParts.SelectionBackground);

        const int horizontalPadding = 10;
        const int badgeHeight = 24;
        var badgeFont = PrintBridgeUiTheme.BadgeFont;
        var textSize = TextRenderer.MeasureText(
            badgeText,
            badgeFont,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding);
        var horizontalMargin = 8;
        var badgeWidth = Math.Min(textSize.Width + horizontalPadding * 2, e.CellBounds.Width - horizontalMargin * 2);
        var badgeLeft = grid.RightToLeft == RightToLeft.Yes
            ? e.CellBounds.Right - badgeWidth - horizontalMargin
            : e.CellBounds.Left + horizontalMargin;
        var badgeRect = new Rectangle(
            badgeLeft,
            e.CellBounds.Top + (e.CellBounds.Height - badgeHeight) / 2,
            badgeWidth,
            badgeHeight);

        using (var path = CreateRoundedRectangle(badgeRect, 6))
        using (var brush = new SolidBrush(backColor))
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        TextRenderer.DrawText(
            e.Graphics,
            badgeText,
            badgeFont,
            badgeRect,
            foreColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        e.Handled = true;
    }
}
