namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// The small native notice the tray shows instead of the app window when the WebView2 Runtime is missing, too old or
/// fails to start. It tells the user to install the runtime and that printing continues; the tray and the engine keep
/// running either way. Drawn in the app's palette (light or dark from the Windows app theme), mirrored for right-to-left
/// languages, scaled for the monitor's DPI, and closed with Enter, Escape or its one button.
/// </summary>
internal sealed class WebView2RuntimeNotice : Form
{
    private const int ContentWidth = 420;
    private readonly bool _dark;

    public WebView2RuntimeNotice(string title, string message, string closeText, bool rightToLeft, bool dark)
    {
        _dark = dark;
        var palette = ShellPalette.For(dark);
        Text = title;
        AccessibleDescription = message;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        // No owner window exists, so the notice keeps a taskbar button and cannot get lost behind other windows.
        ShowInTaskbar = true;
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

        Message = new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            ForeColor = palette.Text,
            Margin = new Padding(0, 0, 0, 16)
        };
        layout.Controls.Add(Message);

        CloseButton = new ShellDialogButton(ShellButtonKind.Primary, palette) { Text = closeText, Margin = Padding.Empty };
        // Modeless: Enter and Escape click this button, which closes the notice.
        CloseButton.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        buttons.Controls.Add(CloseButton);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = CloseButton;
        CancelButton = CloseButton;
    }

    internal Label Message { get; }

    internal Button CloseButton { get; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ShellWindowTheme.TryApplyTitleBar(Handle, _dark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CloseButton.Focus();
    }
}
