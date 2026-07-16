namespace Wasla.PrintBridge.UI;

internal static class PrintBridgeConfirmDialog
{
    public static bool Confirm(
        IWin32Window? owner,
        string title,
        string message,
        string confirmText,
        string cancelText)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
            Font = PrintBridgeUiTheme.SubtitleFont
        };

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var messageLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Text = message,
            Margin = new Padding(0, 0, 0, 16)
        };
        layout.Controls.Add(messageLabel, 0, 0);
        layout.SetColumnSpan(messageLabel, 2);

        var confirmButton = PrintBridgeUiTheme.CreateActionButton(confirmText, primary: true);
        confirmButton.DialogResult = DialogResult.OK;
        confirmButton.Margin = new Padding(0, 0, 6, 0);
        confirmButton.MinimumSize = new Size(100, 34);

        var cancelButton = PrintBridgeUiTheme.CreateActionButton(cancelText);
        cancelButton.DialogResult = DialogResult.Cancel;
        cancelButton.Margin = new Padding(6, 0, 0, 0);
        cancelButton.MinimumSize = new Size(100, 34);

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        buttonPanel.Controls.Add(confirmButton);
        buttonPanel.Controls.Add(cancelButton);
        layout.Controls.Add(buttonPanel, 0, 1);
        layout.SetColumnSpan(buttonPanel, 2);

        form.Controls.Add(layout);
        form.AcceptButton = confirmButton;
        form.CancelButton = cancelButton;

        return form.ShowDialog(owner) == DialogResult.OK;
    }
}
