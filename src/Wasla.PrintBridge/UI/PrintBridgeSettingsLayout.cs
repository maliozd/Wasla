namespace Wasla.PrintBridge.UI;

/// <summary>
/// Static, compact Settings form layout for the Wasla Print Bridge desktop app.
/// Field rows are [label Absolute] [body AutoSize]. Side-action rows put the
/// input and button in one FlowLayoutPanel so the button sits immediately next
/// to the input — never in a separate Absolute column that can drift away when
/// help/status text widens the table.
/// </summary>
internal static class PrintBridgeSettingsLayout
{
    /// <summary>Wide enough for Turkish captions such as "Wasla Web Panel Linki".</summary>
    public const int LabelColumnWidth = 150;

    /// <summary>Fixed width for Göster / Yenile captions (fits Turkish labels comfortably).</summary>
    public const int SideActionButtonWidth = 94;

    /// <summary>Gap between a fixed input and its side action button.</summary>
    public const int SideActionGap = 8;

    /// <summary>Stable single-line control / side-button height.</summary>
    public const int InputRowHeight = 30;

    /// <summary>Fixed TableLayoutPanel row height for input rows (control height + small margin).</summary>
    public const int InputRowStyleHeight = 34;

    /// <summary>Left-aligned settings content cap; blank space on the right is OK.</summary>
    public const int SettingsContentMaxWidth = 940;

    /// <summary>Fixed width for the Server URL textbox (no side button).</summary>
    public const int UrlInputWidth = 700;

    /// <summary>Fixed width for the device token textbox (Göster sits beside it).</summary>
    public const int TokenInputWidth = 640;

    /// <summary>Fixed width for the printer combobox (Yenile sits beside it).</summary>
    public const int PrinterComboWidth = 640;

    /// <summary>Short dropdown width for the language selector.</summary>
    public const int LanguageComboWidth = 330;

    /// <summary>Wrap cap for help/status text under inputs.</summary>
    public const int HelpTextMaxWidth = 700;

    /// <summary>Vertical breathing room above Save/Test/Apply action rows.</summary>
    public const int ActionRowTopMargin = 10;

    public static Panel CreateSectionCard(
        out Label titleLabel,
        out Label descriptionLabel,
        Control content)
    {
        var card = new DoubleBufferedPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = PrintBridgeUiTheme.CardBackground,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 0, 0, 8)
        };
        card.Paint += (_, e) =>
        {
            var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            using var pen = new Pen(PrintBridgeUiTheme.CardBorder);
            e.Graphics.DrawRectangle(pen, rect);
        };

        var stack = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Dock = DockStyle.Top,
            Margin = new Padding(0)
        };
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        titleLabel = CreateSectionTitle(string.Empty);
        descriptionLabel = CreateSectionDescription(string.Empty);
        content.Dock = DockStyle.Top;
        content.Margin = new Padding(0);

        stack.Controls.Add(titleLabel, 0, 0);
        stack.Controls.Add(descriptionLabel, 0, 1);
        stack.Controls.Add(content, 0, 2);
        card.Controls.Add(stack);
        return card;
    }

    /// <summary>
    /// Two-column settings grid: [label Absolute] [body AutoSize].
    /// Side actions live inside the body FlowLayoutPanel, not a third table column.
    /// </summary>
    public static TableLayoutPanel CreateFieldTable(int rowCount)
    {
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = rowCount,
            Dock = DockStyle.Top,
            Margin = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelColumnWidth));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 0; i < rowCount; i++)
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        return table;
    }

    public static void ConfigureInputRow(TableLayoutPanel table, int row) =>
        table.RowStyles[row] = new RowStyle(SizeType.Absolute, InputRowStyleHeight);

    /// <summary>
    /// Left-aligned body that keeps a fixed-width input and optional side button
    /// as one compact pair with a small fixed gap.
    /// </summary>
    public static FlowLayoutPanel CreateInputBody(Control input, Button? sideAction = null)
    {
        var body = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Dock = DockStyle.None,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(0),
            Padding = Padding.Empty
        };

        body.Controls.Add(input);
        if (sideAction is not null)
            body.Controls.Add(sideAction);

        return body;
    }

    /// <summary>
    /// Single-line TextBox/ComboBox at natural height and a fixed compact width.
    /// Never Dock=Fill (stretches vertically) and never anchored Right (stretches wide).
    /// </summary>
    public static void StyleSingleLineInput(Control input, int width)
    {
        input.Dock = DockStyle.None;
        input.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        input.Margin = new Padding(0, 2, 0, 0);
        input.Height = InputRowHeight;
        input.Width = width;
        input.MinimumSize = new Size(Math.Min(80, width), InputRowHeight);
        input.MaximumSize = new Size(width, InputRowHeight);

        if (input is TextBox textBox)
        {
            textBox.Multiline = false;
            textBox.AutoSize = false;
            textBox.WordWrap = false;
        }
        else if (input is ComboBox comboBox)
        {
            comboBox.IntegralHeight = false;
        }
    }

    public static void StyleSideActionButton(Button button)
    {
        StyleInlineButton(button);
        button.AutoSize = false;
        button.Dock = DockStyle.None;
        button.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        button.Width = SideActionButtonWidth;
        button.Height = InputRowHeight;
        button.MinimumSize = new Size(SideActionButtonWidth, InputRowHeight);
        button.MaximumSize = Size.Empty;
        button.Margin = new Padding(SideActionGap, 2, 0, 0);
        button.Padding = new Padding(4, 1, 4, 1);
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.UseCompatibleTextRendering = true;
    }

    public static Label CreateSectionTitle(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            Font = PrintBridgeUiTheme.SectionFont,
            ForeColor = PrintBridgeUiTheme.TextTitle,
            Margin = new Padding(0, 0, 0, 2)
        };

    public static Label CreateSectionDescription(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Font = PrintBridgeUiTheme.HelperFont,
            Margin = new Padding(0, 0, 0, 8)
        };

    public static Label CreateFieldLabel() =>
        new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            ForeColor = PrintBridgeUiTheme.TextTitle,
            Font = PrintBridgeUiTheme.BodyFont,
            Margin = new Padding(0, 6, 8, 0)
        };

    public static Label CreateHelpLabel() =>
        new()
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Font = PrintBridgeUiTheme.HelperFont,
            Margin = new Padding(0, 2, 0, 4),
            MaximumSize = new Size(HelpTextMaxWidth, 0)
        };

    public static Label CreateSectionStatusLabel() =>
        new()
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Visible = false,
            Margin = new Padding(0, 4, 0, 0),
            Font = PrintBridgeUiTheme.HelperFont,
            MaximumSize = new Size(HelpTextMaxWidth, 0)
        };

    public static FlowLayoutPanel CreateActionRow(params Button[] buttons)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Dock = DockStyle.None,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(0, ActionRowTopMargin, 0, 0),
            MaximumSize = new Size(HelpTextMaxWidth, 0)
        };
        foreach (var button in buttons)
            panel.Controls.Add(button);

        return panel;
    }

    public static Control CreateTroubleshootingRow(
        out Label titleLabel,
        out Label helpLabel,
        out Button actionButton)
    {
        var container = new DoubleBufferedPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 2, 0, 0),
            Padding = new Padding(0, 10, 0, 0)
        };
        container.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(230, 234, 239));
            e.Graphics.DrawLine(pen, 0, 0, container.Width - 1, 0);
        };

        var stack = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Dock = DockStyle.Top,
            Margin = new Padding(0)
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        titleLabel = new Label
        {
            AutoSize = true,
            Font = PrintBridgeUiTheme.SectionFont,
            ForeColor = PrintBridgeUiTheme.TextTitle,
            Margin = new Padding(0, 0, 0, 2)
        };

        helpLabel = new Label
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Font = PrintBridgeUiTheme.HelperFont,
            MaximumSize = new Size(HelpTextMaxWidth, 0),
            Margin = new Padding(0)
        };

        actionButton = PrintBridgeUiTheme.CreateRecoveryOutlineButton(string.Empty);
        ApplyTroubleshootingActionButtonLayout(actionButton);

        stack.Controls.Add(titleLabel, 0, 0);
        stack.Controls.Add(helpLabel, 0, 1);
        stack.Controls.Add(actionButton, 0, 2);
        container.Controls.Add(stack);
        return container;
    }

    public static void ApplyTroubleshootingActionButtonLayout(Button button)
    {
        button.AutoSize = true;
        button.Anchor = AnchorStyles.Left;
        button.Dock = DockStyle.None;
        button.MaximumSize = Size.Empty;
        button.MinimumSize = new Size(160, PrintBridgeUiTheme.ActionButtonHeight);
        button.Height = PrintBridgeUiTheme.ActionButtonHeight;
        button.Padding = new Padding(10, 3, 10, 3);
        button.Margin = new Padding(0, 8, 0, 0);
        button.Font = PrintBridgeUiTheme.BodyFont;
        button.UseVisualStyleBackColor = false;
        button.UseCompatibleTextRendering = true;
    }

    public static Label CreateFieldInfoLabel() =>
        new()
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(8, 85, 150),
            Font = PrintBridgeUiTheme.HelperFont,
            Margin = new Padding(0, 2, 0, 4),
            MaximumSize = new Size(HelpTextMaxWidth, 0),
            Visible = false
        };

    public static Label CreateFieldValidationLabel() =>
        new()
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.Danger,
            Font = PrintBridgeUiTheme.HelperFont,
            Margin = new Padding(0, 2, 0, 0),
            Visible = false
        };

    public static void StyleInlineButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = PrintBridgeUiTheme.CardBorder;
        button.BackColor = PrintBridgeUiTheme.CardBackground;
        button.ForeColor = PrintBridgeUiTheme.TextTitle;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(233, 236, 239);
        button.Cursor = Cursors.Hand;
        button.Font = PrintBridgeUiTheme.BodyFont;
        button.Height = InputRowHeight;
        button.UseVisualStyleBackColor = false;
        button.UseCompatibleTextRendering = true;
    }
}
