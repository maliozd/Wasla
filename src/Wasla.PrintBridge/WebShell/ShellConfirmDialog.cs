namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// A native confirmation in the app's design, for actions the page must not be able to confirm by itself
/// (resetting the connection, turning test mode on). Cancel has the initial focus and answers Escape, so an
/// accidental Enter never confirms a harmful action.
/// </summary>
internal sealed class ShellConfirmDialog : Form
{
    private const int ContentWidth = 420;
    private readonly bool _dark;

    private ShellConfirmDialog(string title, string message, string confirmText, string cancelText, bool rightToLeft, bool dark)
    {
        _dark = dark;
        var palette = ShellPalette.For(dark);
        Text = title;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = palette.Surface;
        ForeColor = palette.Text;
        Font = ShellWindowTheme.BodyFont();
        if (rightToLeft)
        {
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
        }

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(22, 20, 22, 18),
            BackColor = palette.Surface
        };

        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        var brandMark = ShellDialogPaint.BrandMark();
        header.Controls.Add(brandMark);
        header.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - brandMark.Width - brandMark.Margin.Horizontal, 0),
            Font = ShellWindowTheme.StrongFont(13F),
            ForeColor = palette.Text,
            Margin = new Padding(0, 2, 0, 0)
        });
        layout.Controls.Add(header);
        layout.Controls.Add(new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            ForeColor = palette.Text,
            Margin = new Padding(0, 0, 0, 16)
        });

        Confirm = new ShellDialogButton(ShellButtonKind.Danger, palette) { Text = confirmText, DialogResult = DialogResult.OK };
        Cancel = new ShellDialogButton(ShellButtonKind.Secondary, palette) { Text = cancelText, DialogResult = DialogResult.Cancel, Margin = Padding.Empty };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        buttons.Controls.Add(Cancel);
        buttons.Controls.Add(Confirm);
        Confirm.TabIndex = 0;
        Cancel.TabIndex = 1;
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        CancelButton = Cancel;
    }

    internal Button Confirm { get; }

    internal Button Cancel { get; }

    public static bool Ask(
        IWin32Window owner,
        string title,
        string message,
        string confirmText,
        string cancelText,
        bool rightToLeft,
        bool dark)
    {
        using var dialog = new ShellConfirmDialog(title, message, confirmText, cancelText, rightToLeft, dark);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ShellWindowTheme.TrySetDarkTitleBar(Handle, _dark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Cancel.Focus();
    }
}
