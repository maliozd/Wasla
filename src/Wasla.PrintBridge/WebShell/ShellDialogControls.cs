using System.Drawing.Drawing2D;
using Wasla.PrintBridge.UI;

namespace Wasla.PrintBridge.WebShell;

internal enum ShellButtonKind
{
    Secondary,
    Primary,
    Danger
}

/// <summary>
/// A button drawn like the app's <c>.pb-button</c>: rounded, border first, the accent only for the main action, and a
/// visible focus ring. It stays a real Windows button, so its name, role, keyboard and Enter/Escape behaviour are the
/// standard ones. <see cref="Inactive"/> makes it look and act unavailable while keeping keyboard focus, like
/// <c>aria-disabled</c> in the page.
/// </summary>
internal sealed class ShellDialogButton : Button
{
    private readonly ShellButtonKind _kind;
    private ShellPalette _palette;
    private bool _hover;
    private bool _inactive;

    public ShellDialogButton(ShellButtonKind kind, ShellPalette palette)
    {
        _kind = kind;
        _palette = palette;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14, 4, 14, 4);
        MinimumSize = new Size(96, 34);
        Margin = new Padding(0, 0, 8, 0);
        Font = ShellWindowTheme.StrongFont();
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    /// <summary>Unavailable but still focusable. Clicks are ignored by the dialog while this is set.</summary>
    public bool Inactive
    {
        get => _inactive;
        set
        {
            if (_inactive == value)
                return;

            _inactive = value;
            Cursor = value ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? _palette.Surface);

        var (fill, border, text) = Colors();
        var scale = DeviceDpi / 96F;
        var radius = 6 * scale;
        var ring = Focused ? 2 * scale : 0;
        var bounds = new RectangleF(ring + 0.5F, ring + 0.5F, Width - 2 * ring - 1, Height - 2 * ring - 1);

        using (var path = ShellDialogPaint.RoundedRectangle(bounds, radius))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var pen = new Pen(border, 1 * scale);
            g.DrawPath(pen, path);
        }

        if (Focused)
        {
            // Matches :focus-visible in the page: a 2 px accent ring outside the control.
            var focus = new RectangleF(1, 1, Width - 2.5F, Height - 2.5F);
            using var path = ShellDialogPaint.RoundedRectangle(focus, radius + ring);
            using var pen = new Pen(_palette.Accent, 2 * scale);
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            Rectangle.Round(bounds),
            text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | (RightToLeft == RightToLeft.Yes ? TextFormatFlags.RightToLeft : TextFormatFlags.Default));
    }

    public void ApplyPalette(ShellPalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    private (Color Fill, Color Border, Color Text) Colors()
    {
        var hover = _hover && !_inactive;
        var colors = _kind switch
        {
            ShellButtonKind.Primary => (hover ? _palette.AccentHover : _palette.Accent, hover ? _palette.AccentHover : _palette.Accent, _palette.AccentText),
            ShellButtonKind.Danger => (hover ? _palette.DangerSoft : _palette.Surface, _palette.Danger, _palette.Danger),
            _ => (hover ? _palette.SurfaceMuted : _palette.Surface, hover ? _palette.Accent : _palette.BorderStrong, _palette.Text)
        };

        return _inactive
            ? (Blend(colors.Item1, _palette.Surface), Blend(colors.Item2, _palette.Surface), Blend(colors.Item3, _palette.Surface))
            : colors;
    }

    // Half-transparent look of aria-disabled buttons in the page.
    private static Color Blend(Color color, Color background) => Color.FromArgb(
        (color.R + background.R) / 2,
        (color.G + background.G) / 2,
        (color.B + background.B) / 2);
}

/// <summary>
/// The frame of an app-style text input: a rounded border that turns accent while the field has focus and danger
/// while it holds an error. The hosted <see cref="TextBox"/> has no border of its own.
/// </summary>
internal sealed class ShellInputFrame : Panel
{
    private ShellPalette _palette;
    private bool _invalid;

    public ShellInputFrame(TextBox input, ShellPalette palette)
    {
        Input = input;
        _palette = palette;
        Padding = new Padding(10, 7, 10, 7);
        Margin = new Padding(0, 2, 0, 2);
        Height = 36;
        BackColor = palette.Surface;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

        input.BorderStyle = BorderStyle.None;
        input.BackColor = palette.Surface;
        input.ForeColor = palette.Text;
        input.Dock = DockStyle.Fill;
        input.GotFocus += (_, _) => Invalidate();
        input.LostFocus += (_, _) => Invalidate();
        Controls.Add(input);

        // Clicking the padding focuses the field, as clicking anywhere in an <input> does.
        Click += (_, _) => input.Focus();
    }

    public TextBox Input { get; }

    public bool Invalid
    {
        get => _invalid;
        set
        {
            if (_invalid == value)
                return;

            _invalid = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? _palette.Surface);

        var scale = DeviceDpi / 96F;
        var focused = Input.Focused;
        var width = (focused || _invalid ? 2 : 1) * scale;
        var bounds = new RectangleF(width / 2 + 0.5F, width / 2 + 0.5F, Width - width - 1, Height - width - 1);
        using var path = ShellDialogPaint.RoundedRectangle(bounds, 6 * scale);
        using var fill = new SolidBrush(_palette.Surface);
        g.FillPath(fill, path);
        using var pen = new Pen(_invalid ? _palette.Danger : focused ? _palette.Accent : _palette.BorderStrong, width);
        g.DrawPath(pen, path);
    }
}

internal static class ShellDialogPaint
{
    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The app's brand mark: the canonical Wasla logo, as in the page header.</summary>
    public static WaslaLogo BrandMark() => new(24) { Margin = new Padding(0, 0, 10, 0) };
}
