namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// The small native notice the tray shows instead of the app window when the WebView2 Runtime is missing, too old or
/// fails to start. It says that printing continues (the service prints; the tray keeps running either way) and how to get
/// the runtime: on a computer Print Bridge's setup installed, its <b>Repair</b> button runs that setup again, which
/// installs the runtime (WAS-62); otherwise it says to run Print Bridge's setup. Drawn in the app's palette (light or dark
/// from the Windows app theme), mirrored for right-to-left languages, scaled for the monitor's DPI. Escape and Close close
/// it; Enter clicks the first button (Repair when it is offered).
/// </summary>
internal sealed class WebView2RuntimeNotice : Form
{
    private const int ContentWidth = 420;
    private readonly bool _dark;

    public WebView2RuntimeNotice(string title, string message, string closeText, bool rightToLeft, bool dark)
        : this(title, message, closeText, rightToLeft, dark, repair: null)
    {
    }

    /// <param name="repair">
    /// The Repair button's text, what it does (true when the setup started) and the text shown when it could not start;
    /// null when there is no installed setup to repair with.
    /// </param>
    public WebView2RuntimeNotice(
        string title,
        string message,
        string closeText,
        bool rightToLeft,
        bool dark,
        (string Text, Func<bool> Start, string FailedMessage)? repair)
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

        CloseButton = new ShellDialogButton(repair is null ? ShellButtonKind.Primary : ShellButtonKind.Secondary, palette)
        {
            Text = closeText,
            Margin = Padding.Empty
        };
        // Modeless: Escape clicks this button, which closes the notice.
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

        if (repair is { } offered)
        {
            RepairButton = new ShellDialogButton(ShellButtonKind.Primary, palette) { Text = offered.Text, Margin = new Padding(0, 0, 8, 0) };
            RepairButton.Click += (_, _) =>
            {
                if (offered.Start())
                {
                    // The setup closes every Print Bridge app before it changes files; this notice is not needed meanwhile.
                    Close();
                    return;
                }

                Message.Text = offered.FailedMessage;
                AccessibleDescription = offered.FailedMessage;
            };
            buttons.Controls.Add(RepairButton);
        }

        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = RepairButton ?? CloseButton;
        CancelButton = CloseButton;
    }

    internal Label Message { get; }

    internal Button CloseButton { get; }

    /// <summary>Runs the installed setup's repair; null when this computer's Print Bridge was not installed by the setup.</summary>
    internal Button? RepairButton { get; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ShellWindowTheme.TryApplyTitleBar(Handle, _dark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        (RepairButton ?? CloseButton).Focus();
    }
}
