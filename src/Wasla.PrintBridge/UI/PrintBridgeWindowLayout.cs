using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.UI;

internal static class PrintBridgeWindowLayout
{
    public const int DefaultClientWidth = PrintBridgeWindowSize.DefaultClientWidth;
    public const int DefaultClientHeight = PrintBridgeWindowSize.DefaultClientHeight;
    public const int MinimumClientWidth = PrintBridgeWindowSize.MinimumClientWidth;
    public const int MinimumClientHeight = PrintBridgeWindowSize.MinimumClientHeight;

    private const int MinimumVisibleWidth = 200;
    private const int MinimumVisibleHeight = 200;

    public static void ApplyStartup(Form form, UiOptions ui)
    {
        // First assign a known client size so we can measure real non-client chrome,
        // then derive an outer MinimumSize that preserves the minimum client area.
        form.MinimumSize = Size.Empty;
        form.ClientSize = new Size(DefaultClientWidth, DefaultClientHeight);

        var nonClientWidth = Math.Max(0, form.Size.Width - form.ClientSize.Width);
        var nonClientHeight = Math.Max(0, form.Size.Height - form.ClientSize.Height);
        if (nonClientWidth <= 0)
            nonClientWidth = PrintBridgeWindowSize.EstimatedNonClientWidth;
        if (nonClientHeight <= 0)
            nonClientHeight = PrintBridgeWindowSize.EstimatedNonClientHeight;

        form.MinimumSize = new Size(
            PrintBridgeWindowSize.OuterWidthForClient(MinimumClientWidth, nonClientWidth),
            PrintBridgeWindowSize.OuterHeightForClient(MinimumClientHeight, nonClientHeight));

        if (ui.WindowWidth is null || ui.WindowHeight is null)
        {
            form.ClientSize = new Size(DefaultClientWidth, DefaultClientHeight);
        }
        else
        {
            var (width, height) = PrintBridgeWindowSize.ResolveStartupOuterSize(ui.WindowWidth, ui.WindowHeight);
            form.Size = new Size(width, height);
            EnsureMinimumClientSize(form);
        }

        if (TryGetSavedLocation(ui, form.Width, form.Height, out var location))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = location;
        }
        else
        {
            form.StartPosition = FormStartPosition.CenterScreen;
        }

        form.WindowState = string.Equals(ui.WindowState, "Maximized", StringComparison.OrdinalIgnoreCase)
            ? FormWindowState.Maximized
            : FormWindowState.Normal;
    }

    public static void CaptureInto(UiOptions ui, Form form)
    {
        if (form.WindowState == FormWindowState.Minimized)
            return;

        Rectangle bounds;
        FormWindowState savedState;

        if (form.WindowState == FormWindowState.Maximized)
        {
            bounds = form.RestoreBounds;
            savedState = FormWindowState.Maximized;
        }
        else
        {
            bounds = form.Bounds;
            savedState = FormWindowState.Normal;
        }

        // Persistence remains outer Size for compatibility with existing settings files.
        ui.WindowWidth = PrintBridgeWindowSize.ClampOuterWidth(bounds.Width);
        ui.WindowHeight = PrintBridgeWindowSize.ClampOuterHeight(bounds.Height);
        ui.WindowLeft = bounds.Left;
        ui.WindowTop = bounds.Top;
        ui.WindowState = savedState == FormWindowState.Maximized ? "Maximized" : "Normal";
    }

    public static void Persist(Form form, PrintBridgeSettingsStore store, PrintBridgeSettingsHolder holder)
    {
        var (hub, bridge, ui) = holder.Snapshot();
        CaptureInto(ui, form);
        store.Save(new PrintBridgeSettingsStore.AppSettingsDocument
        {
            OrderHub = hub,
            PrintBridge = bridge,
            Ui = ui
        });
    }

    private static void EnsureMinimumClientSize(Form form)
    {
        var width = Math.Max(form.ClientSize.Width, MinimumClientWidth);
        var height = Math.Max(form.ClientSize.Height, MinimumClientHeight);
        if (width != form.ClientSize.Width || height != form.ClientSize.Height)
            form.ClientSize = new Size(width, height);
    }

    private static bool TryGetSavedLocation(UiOptions ui, int width, int height, out Point location)
    {
        location = Point.Empty;
        if (ui.WindowLeft is null || ui.WindowTop is null)
            return false;

        var bounds = new Rectangle(ui.WindowLeft.Value, ui.WindowTop.Value, width, height);
        if (!IsVisibleOnScreen(bounds))
            return false;

        location = bounds.Location;
        return true;
    }

    private static bool IsVisibleOnScreen(Rectangle bounds)
    {
        if (bounds.Width < PrintBridgeWindowSize.MinimumOuterWidth
            || bounds.Height < PrintBridgeWindowSize.MinimumOuterHeight)
            return false;

        foreach (var screen in Screen.AllScreens)
        {
            var visible = Rectangle.Intersect(screen.WorkingArea, bounds);
            if (visible.Width >= MinimumVisibleWidth && visible.Height >= MinimumVisibleHeight)
                return true;
        }

        return false;
    }
}
