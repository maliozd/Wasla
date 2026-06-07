namespace OrderHub.PrintBridge.UI;

internal static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(13, 110, 253));
            graphics.FillEllipse(brush, 2, 2, 28, 28);
            using var pen = new Pen(Color.White, 3f);
            graphics.DrawLine(pen, 10, 17, 14, 21);
            graphics.DrawLine(pen, 14, 21, 23, 11);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
