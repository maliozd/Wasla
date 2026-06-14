using Wasla.PrintBridge.Models;

namespace Wasla.PrintBridge.UI;

internal static class TrayIconFactory
{
    public static Icon Create(TrayIconState state = TrayIconState.Connected)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var color = state switch
            {
                TrayIconState.Connected => Color.FromArgb(25, 135, 84),
                TrayIconState.Polling => Color.FromArgb(13, 110, 253),
                TrayIconState.Printing => Color.FromArgb(253, 126, 20),
                TrayIconState.ConnectionLost => Color.FromArgb(220, 53, 69),
                _ => Color.FromArgb(108, 117, 125)
            };

            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, 2, 2, 28, 28);

            using var pen = new Pen(Color.White, 3f);
            switch (state)
            {
                case TrayIconState.Printing:
                    graphics.FillRectangle(Brushes.White, 10, 11, 12, 8);
                    graphics.DrawRectangle(pen, 10, 11, 12, 8);
                    graphics.DrawLine(pen, 12, 19, 20, 19);
                    break;
                case TrayIconState.ConnectionLost:
                    graphics.DrawLine(pen, 11, 11, 21, 21);
                    graphics.DrawLine(pen, 21, 11, 11, 21);
                    break;
                default:
                    graphics.DrawLine(pen, 10, 17, 14, 21);
                    graphics.DrawLine(pen, 14, 21, 23, 11);
                    break;
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
