namespace Wasla.PrintBridge.UI;

internal static class PrintBridgeRtl
{
    public static void Apply(Form form, bool isRtl)
    {
        form.RightToLeft = isRtl ? RightToLeft.Yes : RightToLeft.No;
        form.RightToLeftLayout = isRtl;
        ApplyRecursive(form, isRtl);
    }

    private static void ApplyRecursive(Control control, bool isRtl)
    {
        foreach (Control child in control.Controls)
        {
            switch (child)
            {
                case FlowLayoutPanel flow:
                    flow.FlowDirection = isRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                    break;
                case DataGridView grid:
                    grid.RightToLeft = isRtl ? RightToLeft.Yes : RightToLeft.No;
                    grid.ColumnHeadersDefaultCellStyle.Alignment = isRtl
                        ? DataGridViewContentAlignment.MiddleRight
                        : DataGridViewContentAlignment.MiddleLeft;
                    grid.DefaultCellStyle.Alignment = isRtl
                        ? DataGridViewContentAlignment.MiddleRight
                        : DataGridViewContentAlignment.MiddleLeft;
                    break;
                case Label { AutoSize: true } label when label.Parent is TableLayoutPanel:
                    label.TextAlign = isRtl ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
                    break;
            }

            ApplyRecursive(child, isRtl);
        }
    }
}
