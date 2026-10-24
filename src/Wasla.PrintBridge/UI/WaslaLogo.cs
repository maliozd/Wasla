using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Wasla.PrintBridge.UI;

/// <summary>
/// The canonical Wasla logo in the native windows. It fills the vector paths of the embedded
/// <c>wasla-logo.svg</c> (the file Wasla Web serves and the WebView2 page shows), so it is sharp at every DPI,
/// keeps the logo's aspect ratio and colours, and is never redrawn or approximated by hand.
/// </summary>
internal sealed class WaslaLogo : Control
{
    private const uint LayoutRtl = 0x00000001;

    /// <param name="height">Logical height at 96 DPI; the width follows the logo's aspect ratio.</param>
    public WaslaLogo(int height)
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor,
            true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = Color.Transparent;
        Size = new Size((int)Math.Round(height * WaslaLogoShape.AspectRatio), height);
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Wasla";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Draw(e.Graphics, ClientSize);
    }

    /// <summary>Draws the logo centred in <paramref name="size"/>, scaled uniformly so it is never stretched.</summary>
    internal static void Draw(Graphics g, Size size)
    {
        var shape = WaslaLogoShape.Instance;
        if (shape is null || size.Width <= 0 || size.Height <= 0)
            return;

        var mirrored = IsDeviceContextMirrored(g);
        var state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // In a right-to-left window (RightToLeftLayout) Windows mirrors the drawing surface; undo that so the
            // wordmark is never shown back to front. The control's position is still mirrored by its parent.
            if (mirrored)
            {
                g.TranslateTransform(size.Width, 0);
                g.ScaleTransform(-1, 1);
            }

            var viewBox = shape.ViewBox;
            var scale = Math.Min(size.Width / viewBox.Width, size.Height / viewBox.Height);
            g.TranslateTransform((size.Width - viewBox.Width * scale) / 2, (size.Height - viewBox.Height * scale) / 2);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-viewBox.X, -viewBox.Y);

            foreach (var part in shape.Parts)
            {
                using var brush = new SolidBrush(part.Fill);
                g.FillPath(brush, part.Path);
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    private static bool IsDeviceContextMirrored(Graphics g)
    {
        var hdc = g.GetHdc();
        try
        {
            return (GetLayout(hdc) & LayoutRtl) != 0;
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern uint GetLayout(IntPtr hdc);
}

internal sealed record WaslaLogoPart(Color Fill, GraphicsPath Path);

/// <summary>
/// Reads the embedded logo SVG. It supports exactly what the canonical file uses — a <c>viewBox</c>, group
/// <c>fill</c> colours, <c>translate</c> transforms and absolute M/L/C/Z path commands — and rejects anything else,
/// so a changed logo file fails the tests instead of drawing wrongly.
/// </summary>
internal sealed partial class WaslaLogoShape
{
    internal const string ResourceName = "Wasla.PrintBridge.Brand.wasla-logo.svg";

    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly Lazy<WaslaLogoShape?> Embedded = new(LoadEmbeddedOrNull);

    private WaslaLogoShape(RectangleF viewBox, IReadOnlyList<WaslaLogoPart> parts)
    {
        ViewBox = viewBox;
        Parts = parts;
    }

    public RectangleF ViewBox { get; }

    public IReadOnlyList<WaslaLogoPart> Parts { get; }

    /// <summary>The embedded logo, or null if it could not be read (the logo is then simply not drawn).</summary>
    public static WaslaLogoShape? Instance => Embedded.Value;

    /// <summary>Width divided by height of the embedded logo; 1 if it could not be read.</summary>
    public static float AspectRatio => Instance is { } shape ? shape.ViewBox.Width / shape.ViewBox.Height : 1F;

    public static WaslaLogoShape LoadEmbedded()
    {
        using var stream = typeof(WaslaLogoShape).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"Embedded resource '{ResourceName}' is missing.");
        return Load(stream);
    }

    public static WaslaLogoShape Load(Stream svg)
    {
        var root = XDocument.Load(svg).Root ?? throw new InvalidDataException("The logo SVG is empty.");
        var box = Numbers((string?)root.Attribute("viewBox"));
        if (box.Length != 4 || box[2] <= 0 || box[3] <= 0)
            throw new InvalidDataException("The logo SVG needs a positive viewBox.");

        var parts = new List<WaslaLogoPart>();
        foreach (var path in root.Descendants(Svg + "path"))
        {
            Color? fill = null;
            var offset = PointF.Empty;
            foreach (var element in path.AncestorsAndSelf())
            {
                if (fill is null && (string?)element.Attribute("fill") is { } color)
                    fill = ColorTranslator.FromHtml(color);
                if ((string?)element.Attribute("transform") is { } transform)
                {
                    var translate = Translate(transform);
                    offset = new PointF(offset.X + translate.X, offset.Y + translate.Y);
                }
            }

            parts.Add(new WaslaLogoPart(
                fill ?? Color.Black,
                Path((string?)path.Attribute("d") ?? throw new InvalidDataException("A logo path has no data."), offset)));
        }

        if (parts.Count == 0)
            throw new InvalidDataException("The logo SVG has no paths.");

        return new WaslaLogoShape(new RectangleF(box[0], box[1], box[2], box[3]), parts);
    }

    private static WaslaLogoShape? LoadEmbeddedOrNull()
    {
        try
        {
            return LoadEmbedded();
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or FormatException)
        {
            return null;
        }
    }

    private static GraphicsPath Path(string data, PointF offset)
    {
        // SVG's default fill rule is nonzero, which is GDI+ winding mode.
        var path = new GraphicsPath(FillMode.Winding);
        var tokens = PathTokenRegex().Matches(data).Select(m => m.Value).ToArray();
        var index = 0;
        var command = '\0';
        var current = PointF.Empty;

        PointF Next()
        {
            if (index + 1 >= tokens.Length || char.IsLetter(tokens[index][0]) || char.IsLetter(tokens[index + 1][0]))
                throw new InvalidDataException("A logo path command is missing coordinates.");
            var point = new PointF(Number(tokens[index]) + offset.X, Number(tokens[index + 1]) + offset.Y);
            index += 2;
            return point;
        }

        while (index < tokens.Length)
        {
            if (char.IsLetter(tokens[index][0]))
            {
                command = tokens[index][0];
                index++;
                if (command == 'Z')
                {
                    path.CloseFigure();
                    continue;
                }
            }

            switch (command)
            {
                case 'M':
                    path.StartFigure();
                    current = Next();
                    command = 'L'; // Further coordinate pairs after a moveto are linetos.
                    break;
                case 'L':
                    var end = Next();
                    path.AddLine(current, end);
                    current = end;
                    break;
                case 'C':
                    var c1 = Next();
                    var c2 = Next();
                    var to = Next();
                    path.AddBezier(current, c1, c2, to);
                    current = to;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported logo path command '{command}'.");
            }
        }

        return path;
    }

    private static PointF Translate(string transform)
    {
        var match = TranslateRegex().Match(transform);
        if (!match.Success)
            throw new InvalidDataException($"Unsupported logo transform '{transform}'.");
        return new PointF(Number(match.Groups[1].Value), Number(match.Groups[2].Value));
    }

    private static float[] Numbers(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(Number).ToArray();

    private static float Number(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex PathTokenRegex();

    [GeneratedRegex(@"^\s*translate\(\s*([-+]?[\d.eE+-]+)\s*[,\s]\s*([-+]?[\d.eE+-]+)\s*\)\s*$")]
    private static partial Regex TranslateRegex();
}
