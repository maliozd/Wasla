using System.Runtime.InteropServices;

namespace OrderHub.PrintBridge.UI;

internal static class TextBoxScrollHelper
{
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int EmGetLineCount = 0x00BA;
    private const int EmLineScroll = 0x00B6;

    public static int NearBottomThresholdPixels { get; set; } = 32;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    public static int GetFirstVisibleLine(TextBox textBox) =>
        textBox.IsHandleCreated
            ? SendMessage(textBox.Handle, EmGetFirstVisibleLine, 0, 0)
            : 0;

    public static bool IsNearBottom(TextBox textBox)
    {
        if (!textBox.IsHandleCreated || textBox.TextLength == 0)
            return true;

        var lastCharIndex = Math.Max(0, textBox.TextLength - 1);
        var lastCharPos = textBox.GetPositionFromCharIndex(lastCharIndex);
        if (lastCharPos.X < 0 && lastCharPos.Y < 0)
            return false;

        var lineHeight = Math.Max(1, TextRenderer.MeasureText("Ag", textBox.Font).Height);
        if (lastCharPos.Y + lineHeight <= textBox.ClientSize.Height + NearBottomThresholdPixels)
            return true;

        var firstVisible = GetFirstVisibleLine(textBox);
        var lineCount = SendMessage(textBox.Handle, EmGetLineCount, 0, 0);
        if (lineCount <= 0)
            return true;

        var visibleLines = Math.Max(1, (textBox.ClientSize.Height + lineHeight - 1) / lineHeight);
        var lastVisibleLine = firstVisible + visibleLines - 1;
        return lastVisibleLine >= lineCount - 1;
    }

    public static void ScrollToBottom(TextBox textBox)
    {
        if (!textBox.IsHandleCreated)
            return;

        textBox.SelectionStart = textBox.TextLength;
        textBox.SelectionLength = 0;
        textBox.ScrollToCaret();
    }

    public static void RestoreFirstVisibleLine(TextBox textBox, int line)
    {
        if (!textBox.IsHandleCreated)
            return;

        var target = Math.Max(0, line);
        var current = GetFirstVisibleLine(textBox);
        var delta = target - current;
        if (delta != 0)
            SendMessage(textBox.Handle, EmLineScroll, 0, delta);
    }
}
