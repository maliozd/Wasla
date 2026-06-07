namespace OrderHub.PrintBridge.UI;

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

    public static Font TitleFont => new("Segoe UI Semibold", 14F, FontStyle.Bold);
    public static Font SubtitleFont => new("Segoe UI", 9.5F, FontStyle.Regular);
    public static Font BadgeFont => new("Segoe UI Semibold", 9F, FontStyle.Bold);
    public static Font SectionFont => new("Segoe UI Semibold", 10F, FontStyle.Bold);

    public static Panel CreateCard(string title, out TableLayoutPanel contentTable, int rows)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBackground,
            Padding = new Padding(14, 12, 14, 12),
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
            Height = 34,
            MinimumSize = new Size(120, 34),
            Padding = new Padding(12, 4, 12, 4),
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 10, 0),
            Cursor = Cursors.Hand
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

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = CardBackground;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(233, 236, 239);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(248, 249, 250);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextTitle;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6);
        grid.ColumnHeadersHeight = 32;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(207, 226, 255);
        grid.DefaultCellStyle.SelectionForeColor = TextTitle;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 249, 250);
        grid.RowTemplate.Height = 28;
    }
}
