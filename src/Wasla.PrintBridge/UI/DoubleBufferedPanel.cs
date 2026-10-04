namespace Wasla.PrintBridge.UI;

/// <summary>
/// Panel with WinForms double-buffering enabled to reduce flicker while resizing.
/// </summary>
internal sealed class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        UpdateStyles();
    }
}
