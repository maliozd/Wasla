using OrderHub.PrintBridge.Options;
using OrderHub.PrintBridge.Services;

namespace OrderHub.PrintBridge.UI;

internal static class PrintBridgeWindowLayout
{
    public const int DefaultWidth = 1600;
    public const int DefaultHeight = 900;
    public const int MinimumWidth = 1200;
    public const int MinimumHeight = 800;
    private const int MinimumVisibleWidth = 200;
    private const int MinimumVisibleHeight = 200;

    public static void ApplyStartup(Form form, UiOptions ui)
    {
        form.MinimumSize = new Size(MinimumWidth, MinimumHeight);

        var width = ClampWidth(ui.WindowWidth ?? DefaultWidth);
        var height = ClampHeight(ui.WindowHeight ?? DefaultHeight);
        form.Size = new Size(width, height);

        if (TryGetSavedLocation(ui, width, height, out var location))
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

        ui.WindowWidth = ClampWidth(bounds.Width);
        ui.WindowHeight = ClampHeight(bounds.Height);
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

    private static int ClampWidth(int width) => Math.Clamp(width, MinimumWidth, 10000);

    private static int ClampHeight(int height) => Math.Clamp(height, MinimumHeight, 10000);

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
        if (bounds.Width < MinimumWidth || bounds.Height < MinimumHeight)
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
