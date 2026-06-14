namespace Wasla.PrintBridge.UI;

/// <summary>
/// Read-only log viewer text box that raises scroll events for follow/jump behavior.
/// </summary>
internal sealed class LogTextBox : TextBox
{
    private const int WmVscroll = 0x0115;
    private const int WmMouseWheel = 0x020A;
    private const int WmKeyDown = 0x0100;

    public event EventHandler? UserScrolled;

    public LogTextBox()
    {
        Multiline = true;
        ReadOnly = true;
        ScrollBars = ScrollBars.Both;
        WordWrap = false;
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        switch (m.Msg)
        {
            case WmVscroll:
            case WmMouseWheel:
                RaiseUserScrolled();
                break;
            case WmKeyDown:
                var key = (Keys)(int)m.WParam;
                if (key is Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
                    or Keys.Up or Keys.Down)
                {
                    BeginInvoke(RaiseUserScrolled);
                }

                break;
        }
    }

    private void RaiseUserScrolled() => UserScrolled?.Invoke(this, EventArgs.Empty);
}
