using Wasla.PrintBridge.Models;

namespace Wasla.PrintBridge.UI;

internal static class PrintBridgeUiTheme
{
    public static readonly Color PageBackground = Color.FromArgb(245, 247, 250);
    public static readonly Color CardBackground = Color.White;
    public static readonly Color CardBorder = Color.FromArgb(218, 224, 232);
    public static readonly Color TextMuted = Color.FromArgb(95, 105, 118);
    public static readonly Color TextTitle = Color.FromArgb(33, 37, 41);
    public static readonly Color Success = Color.FromArgb(25, 135, 84);
    public static readonly Color Danger = Color.FromArgb(220, 53, 69);
    public static readonly Color Info = Color.FromArgb(13, 110, 253);
    public static readonly Color Warning = Color.FromArgb(180, 130, 0);
    public static readonly Color Inactive = Color.FromArgb(108, 117, 125);
    public static readonly Color PrimaryButton = Color.FromArgb(13, 110, 253);
    public static readonly Color PrimaryButtonHover = Color.FromArgb(11, 94, 215);

    public static Font TitleFont => new("Segoe UI Semibold", 12F, FontStyle.Bold);
    public static Font SubtitleFont => new("Segoe UI", 9F, FontStyle.Regular);
    public static Font BadgeFont => new("Segoe UI Semibold", 8.5F, FontStyle.Bold);
    public static Font SectionFont => new("Segoe UI Semibold", 10F, FontStyle.Bold);
    public static Font BodyFont => new("Segoe UI", 9F, FontStyle.Regular);
    public static Font HelperFont => new("Segoe UI", 8.25F, FontStyle.Regular);
    public static Font FooterFont => new("Segoe UI", 8.25F, FontStyle.Regular);
    public static Font MetricLabelFont => new("Segoe UI", 8.5F, FontStyle.Regular);
    public static Font MetricValueFont => new("Segoe UI Semibold", 13.5F, FontStyle.Bold);
    public static Font MetricStatusFont => new("Segoe UI Semibold", 10.5F, FontStyle.Bold);
    public static Font MetricTimestampFont => new("Segoe UI Semibold", 10F, FontStyle.Bold);
    public static Size MetricCardSize => new(152, 84);
    public static int MetricCardSpacing => 8;
    public static int ActionButtonHeight => 30;
    public static int ToolbarControlHeight => 30;
    public static int GridRowHeight => 38;
    public static int GridHeaderHeight => 36;

    public static Panel CreateCard(string title, out TableLayoutPanel contentTable, int rows)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBackground,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 0, 8, 8)
        };
        card.Paint += (_, e) =>
        {
            var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            using var pen = new Pen(CardBorder);
            e.Graphics.DrawRectangle(pen, rect);
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var titleLabel = new Label
        {
            Text = title,
            Font = SectionFont,
            ForeColor = TextTitle,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        layout.Controls.Add(titleLabel, 0, 0);

        contentTable = CreateTwoColumnTable(rows);
        layout.Controls.Add(contentTable, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    public static TableLayoutPanel CreateTwoColumnTable(int rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < rows; i++)
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return table;
    }

    public static Label AddStatusRow(TableLayoutPanel table, string label, int row)
    {
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = TextMuted,
            Margin = new Padding(0, 2, 8, 2)
        }, 0, row);

        var value = new Label
        {
            Text = "-",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = TextTitle,
            Margin = new Padding(0, 2, 0, 2)
        };
        table.Controls.Add(value, 1, row);
        return value;
    }

    public static Button CreateActionButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = ActionButtonHeight,
            MinimumSize = new Size(80, ActionButtonHeight),
            Padding = new Padding(8, 2, 8, 2),
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Font = BodyFont
        };
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = CardBorder;
        if (primary)
        {
            button.BackColor = PrimaryButton;
            button.ForeColor = Color.White;
            button.FlatAppearance.MouseOverBackColor = PrimaryButtonHover;
        }
        else
        {
            button.BackColor = CardBackground;
            button.ForeColor = TextTitle;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(233, 236, 239);
        }

        return button;
    }

    public static Button CreateToolbarPrimaryButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Height = ToolbarControlHeight,
            MinimumSize = new Size(0, ToolbarControlHeight),
            MaximumSize = new Size(0, ToolbarControlHeight),
            Padding = new Padding(8, 0, 8, 0),
            Margin = new Padding(0, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            UseCompatibleTextRendering = true,
            Cursor = Cursors.Hand,
            Font = BodyFont
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = PrimaryButtonHover;
        button.EnabledChanged += (_, _) => ApplyToolbarPrimaryButtonAppearance(button);
        ApplyToolbarPrimaryButtonAppearance(button);
        ApplyToolbarPrimaryButtonWidth(button);
        return button;
    }

    public static void ApplyToolbarPrimaryButtonWidth(Button button) =>
        ApplyLocalizedButtonWidth(button, height: ToolbarControlHeight, horizontalPadding: 2);

    public static void ApplyRecoveryOutlineButtonWidth(Button button) =>
        ApplyLocalizedButtonWidth(button, height: ActionButtonHeight, horizontalPadding: 6);

    public static void ApplyLocalizedButtonWidth(Button button, int height, int horizontalPadding = 4)
    {
        button.AutoSize = false;
        button.Height = height;
        button.MinimumSize = new Size(0, height);
        button.MaximumSize = new Size(0, height);

        var preferredWidth = button.GetPreferredSize(Size.Empty).Width;
        if (preferredWidth <= height && !string.IsNullOrEmpty(button.Text))
        {
            preferredWidth = TextRenderer.MeasureText(
                button.Text,
                button.Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width
                + button.Padding.Horizontal
                + 8;
        }

        var width = Math.Max(preferredWidth + horizontalPadding, height);
        button.Width = width;
        button.MinimumSize = new Size(width, height);
    }

    public static void ApplyToolbarPrimaryButtonAppearance(Button button)
    {
        if (button.Enabled)
        {
            button.BackColor = PrimaryButton;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = PrimaryButton;
            button.FlatAppearance.MouseOverBackColor = PrimaryButtonHover;
            return;
        }

        button.BackColor = Color.FromArgb(186, 201, 220);
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Color.FromArgb(186, 201, 220);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(186, 201, 220);
    }

    public static Button CreateDangerOutlineButton(string text) =>
        CreateRecoveryOutlineButton(text);

    public static Button CreateRecoveryOutlineButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(160, ActionButtonHeight),
            Height = ActionButtonHeight,
            Padding = new Padding(10, 3, 10, 3),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Margin = new Padding(0, 8, 0, 0),
            Anchor = AnchorStyles.Left,
            Cursor = Cursors.Hand,
            BackColor = CardBackground,
            ForeColor = Color.FromArgb(146, 64, 74),
            Font = BodyFont
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(228, 181, 186);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 248, 249);
        return button;
    }

    public static Panel CreateMetricCard(
        string title,
        out Label valueLabel,
        out Label titleLabel,
        Font? valueFont = null,
        bool valueAutoEllipsis = true)
    {
        var card = new Panel
        {
            Dock = DockStyle.None,
            BackColor = CardBackground,
            Padding = new Padding(8, 6, 8, 6),
            Margin = new Padding(0, 0, MetricCardSpacing, MetricCardSpacing),
            MinimumSize = MetricCardSize,
            Size = MetricCardSize
        };
        card.Paint += (_, e) =>
        {
            var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            using var pen = new Pen(CardBorder);
            e.Graphics.DrawRectangle(pen, rect);
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        titleLabel = new Label
        {
            Text = title,
            Font = MetricLabelFont,
            ForeColor = TextMuted,
            AutoSize = false,
            Height = 26,
            Dock = DockStyle.Top,
            AutoEllipsis = true,
            Margin = new Padding(0, 0, 0, 1)
        };

        valueLabel = new Label
        {
            Text = "-",
            Font = valueFont ?? MetricValueFont,
            ForeColor = TextTitle,
            AutoSize = false,
            Height = 32,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = valueAutoEllipsis,
            Margin = new Padding(0)
        };

        layout.Controls.Add(titleLabel, 0, 0);
        layout.Controls.Add(valueLabel, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    public static void StyleSettingsTextBox(TextBox textBox)
    {
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.Font = BodyFont;
        textBox.Margin = new Padding(0);
        textBox.Multiline = false;
        textBox.AutoSize = false;
    }

    public static void StyleSettingsComboBox(ComboBox comboBox)
    {
        comboBox.Font = BodyFont;
        comboBox.Margin = new Padding(0);
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = CardBackground;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(233, 236, 239);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(248, 249, 250);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextTitle;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.75F, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4);
        grid.ColumnHeadersHeight = GridHeaderHeight;
        grid.DefaultCellStyle.Font = BodyFont;
        grid.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(207, 226, 255);
        grid.DefaultCellStyle.SelectionForeColor = TextTitle;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 249, 250);
        grid.RowTemplate.Height = GridRowHeight;
        grid.RowTemplate.MinimumHeight = 34;
    }

    public static (Color BackColor, Color ForeColor) GetStatusBadgeColors(LocalPrintJobStatus status) =>
        status switch
        {
            LocalPrintJobStatus.Printed => (Color.FromArgb(212, 237, 220), Color.FromArgb(21, 87, 36)),
            LocalPrintJobStatus.Printing => (Color.FromArgb(207, 226, 255), Color.FromArgb(8, 66, 152)),
            LocalPrintJobStatus.Received => (Color.FromArgb(255, 243, 205), Color.FromArgb(133, 100, 4)),
            LocalPrintJobStatus.Failed => (Color.FromArgb(248, 215, 218), Color.FromArgb(114, 28, 36)),
            LocalPrintJobStatus.Skipped => (Color.FromArgb(233, 236, 239), Inactive),
            _ => (CardBackground, TextTitle)
        };
}
