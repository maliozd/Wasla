using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.WebShell;

/// <summary>One labelled value under a confirmation's message; <see cref="LeftToRight"/> keeps addresses unmirrored.</summary>
internal readonly record struct ShellConfirmDetail(string Label, string Value, bool LeftToRight = false);

/// <summary>
/// A native confirmation in the app's design, for actions the page must not be able to confirm by itself
/// (resetting the connection, turning test mode on, moving to another restaurant from a setup link). Cancel has the
/// initial focus and answers Escape, and closing the window cancels too, so an accidental Enter never confirms a
/// harmful action.
/// </summary>
internal sealed class ShellConfirmDialog : Form
{
    private const int ContentWidth = 420;
    private readonly bool _dark;

    private ShellConfirmDialog(
        string title,
        string message,
        string confirmText,
        string cancelText,
        bool rightToLeft,
        bool dark,
        IReadOnlyList<ShellConfirmDetail>? details = null)
    {
        _dark = dark;
        var palette = ShellPalette.For(dark);
        Text = title;
        AccessibleName = title;
        AccessibleDescription = message;
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
        Message = new Label
        {
            Text = message,
            AutoSize = true,
            UseMnemonic = false,
            MaximumSize = new Size(ContentWidth, 0),
            ForeColor = palette.Text,
            Margin = new Padding(0, 0, 0, 16)
        };
        layout.Controls.Add(Message);

        if (details is { Count: > 0 })
            layout.Controls.Add(BuildDetails(details, palette));

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

    internal Label Message { get; }

    /// <summary>The value labels of the details, in order (for tests).</summary>
    internal List<Label> DetailValues { get; } = [];

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

    /// <summary>
    /// The confirmation before a setup link moves Print Bridge to another restaurant (WAS-91): the restaurants as the
    /// server verified them and both servers, nothing secret. It defaults to Cancel; only the danger button approves.
    /// </summary>
    public static ShellConfirmDialog ForConnectionTransfer(
        PrintBridgeConnectionTransferConsentRequest request,
        PrintBridgeLocalizer localizer,
        bool rightToLeft,
        bool dark)
    {
        var current = NameOrUnknown(request.CurrentTenantName, localizer);
        var target = NameOrUnknown(request.TargetTenantName, localizer);
        return new ShellConfirmDialog(
            localizer["Auto.Transfer.Title"],
            localizer.GetString("Auto.Transfer.Message", current, target),
            localizer["Auto.Transfer.Confirm"],
            localizer["Shell.Action.Cancel"],
            rightToLeft,
            dark,
            [
                new(localizer["Auto.Transfer.CurrentRestaurant"], current),
                new(localizer["Auto.Transfer.CurrentServer"], request.CurrentServer, LeftToRight: true),
                new(localizer["Auto.Transfer.NewRestaurant"], target),
                new(localizer["Auto.Transfer.NewServer"], request.TargetServer, LeftToRight: true)
            ]);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ShellWindowTheme.TryApplyTitleBar(Handle, _dark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Cancel.Focus();
    }

    private TableLayoutPanel BuildDetails(IReadOnlyList<ShellConfirmDetail> details, ShellPalette palette)
    {
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = details.Count,
            BackColor = palette.SurfaceMuted,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 0, 0, 18)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (var detail in details)
        {
            var label = new Label
            {
                Text = detail.Label,
                AutoSize = true,
                ForeColor = palette.TextSecondary,
                BackColor = palette.SurfaceMuted,
                Margin = new Padding(0, 3, 14, 3)
            };
            var value = new Label
            {
                Text = detail.Value,
                AutoSize = true,
                UseMnemonic = false,
                MaximumSize = new Size(ContentWidth - 170, 0),
                Font = ShellWindowTheme.StrongFont(),
                ForeColor = palette.Text,
                BackColor = palette.SurfaceMuted,
                Margin = new Padding(0, 3, 0, 3),
                AccessibleName = detail.Label,
                AccessibleDescription = detail.Value
            };

            // An address reads left to right in every language, also inside a mirrored dialog.
            if (detail.LeftToRight)
                value.RightToLeft = RightToLeft.No;

            table.Controls.Add(label);
            table.Controls.Add(value);
            DetailValues.Add(value);
        }

        return table;
    }

    private static string NameOrUnknown(string name, PrintBridgeLocalizer localizer) =>
        string.IsNullOrWhiteSpace(name) ? localizer["Auto.Transfer.UnknownRestaurant"] : name;
}
