using System.Windows.Forms.Automation;
using Wasla.PrintBridge.Localization;

namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// The native window where the WebView2 app's user enters the Wasla server URL and device token. It belongs to the
/// host, not to the page: the page only asks for it and later receives a localized result. The token field starts
/// empty and masked. Show only reveals what was typed here; the saved token is never loaded into this window, so it
/// cannot be shown again. Cancel, Escape or closing before saving begins leave the saved settings as they were. All
/// rules live in <see cref="ShellConnectionSetup"/>; this class is layout, focus and announcements.
/// </summary>
internal sealed class ShellConnectionDialog : Form
{
    private const int ContentWidth = 440;

    private readonly ShellConnectionSetup _setup;
    private readonly PrintBridgeLocalizer _localizer;
    private readonly ShellPalette _palette;
    private readonly ShellConnectionSetupMode _mode;

    private readonly TextBox _serverUrl;
    private readonly TextBox _token;
    private readonly ShellInputFrame _serverUrlFrame;
    private readonly ShellInputFrame _tokenFrame;
    private readonly ShellDialogButton _toggleToken;
    private readonly ShellDialogButton _connect;
    private readonly ShellDialogButton _cancel;
    private readonly Label _serverUrlHelp;
    private readonly Label _tokenHelp;
    private readonly Label _status;

    private CancellationTokenSource? _verification;
    private bool _busy;
    private bool _saving;

    public ShellConnectionDialog(ShellConnectionSetup setup, PrintBridgeLocalizer localizer, bool rightToLeft, bool dark)
    {
        _setup = setup;
        _localizer = localizer;
        _palette = ShellPalette.For(dark);
        _mode = setup.Mode;

        Text = _localizer[TitleKey(_mode)];
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = _palette.Surface;
        ForeColor = _palette.Text;
        Font = ShellWindowTheme.BodyFont();
        KeyPreview = true;
        if (rightToLeft)
        {
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
        }

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(22, 20, 22, 18),
            BackColor = _palette.Surface
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var header = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 10)
        };
        header.Controls.Add(ShellDialogPaint.BrandMark(_palette));
        header.Controls.Add(new Label
        {
            Text = _localizer[TitleKey(_mode)],
            AutoSize = true,
            Font = ShellWindowTheme.StrongFont(13F),
            ForeColor = _palette.Text,
            Margin = new Padding(0, 2, 0, 0)
        });
        layout.Controls.Add(header);
        layout.Controls.Add(Paragraph(_localizer[IntroKey(_mode)], _palette.Text, bottom: 14));

        _serverUrl = new TextBox
        {
            MaxLength = ShellConnectionSetup.MaxServerUrlLength,
            Text = setup.SavedServerUrl,
            // URLs and tokens read left to right in every language.
            RightToLeft = RightToLeft.No,
            Font = ShellWindowTheme.BodyFont()
        };
        _serverUrlFrame = new ShellInputFrame(_serverUrl, _palette) { Width = ContentWidth };
        _serverUrlHelp = Paragraph(_localizer["Settings.ServerUrlHelp"], _palette.TextSecondary, bottom: 12, small: true);
        AddField(layout, _localizer["Settings.ServerUrl"], _serverUrlFrame, _serverUrlHelp);

        _token = new TextBox
        {
            MaxLength = ShellConnectionSetup.MaxTokenLength,
            UseSystemPasswordChar = true,
            RightToLeft = RightToLeft.No,
            Font = ShellWindowTheme.BodyFont()
        };
        _tokenFrame = new ShellInputFrame(_token, _palette) { Width = ContentWidth - 96 - 8, Margin = new Padding(0, 2, 8, 2) };
        _toggleToken = new ShellDialogButton(ShellButtonKind.Secondary, _palette)
        {
            Text = _localizer["Button.ShowToken"],
            MinimumSize = new Size(96, 36),
            Margin = new Padding(0, 2, 0, 2)
        };
        _toggleToken.Click += (_, _) => ToggleTokenVisibility();
        var tokenRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        tokenRow.Controls.Add(_tokenFrame);
        tokenRow.Controls.Add(_toggleToken);
        var tokenHelpText = _setup.CanKeepSavedToken
            ? _localizer["Shell.Setup.TokenKeepHint"]
            : _localizer["Settings.AgentTokenHelp"];
        _tokenHelp = Paragraph(tokenHelpText, _palette.TextSecondary, bottom: 12, small: true);
        AddField(layout, _localizer["Settings.AgentToken"], tokenRow, _tokenHelp, labelFor: _token);

        _status = Paragraph(string.Empty, _palette.TextSecondary, bottom: 12);
        _status.Visible = false;
        _status.AccessibleRole = AccessibleRole.Alert;
        layout.Controls.Add(_status);

        _connect = new ShellDialogButton(ShellButtonKind.Primary, _palette) { Text = _localizer["Shell.Setup.Connect"] };
        _cancel = new ShellDialogButton(ShellButtonKind.Secondary, _palette) { Text = _localizer["Shell.Action.Cancel"], Margin = Padding.Empty };
        _connect.Click += async (_, _) => await ConnectAsync();
        _cancel.Click += (_, _) => CancelAndClose();
        var buttons = new FlowLayoutPanel
        {
            // Added Cancel first: the right-to-left flow puts Connect before it at the trailing edge, as in Windows dialogs.
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 0)
        };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_connect);
        // Tab follows what is seen: Connect, then Cancel.
        _connect.TabIndex = 0;
        _cancel.TabIndex = 1;
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = _connect;
        CancelButton = _cancel;

        _serverUrl.TextChanged += (_, _) => ClearFieldError(_serverUrlFrame, _serverUrlHelp);
        _token.TextChanged += (_, _) => ClearFieldError(_tokenFrame, _tokenHelp);
    }

    /// <summary>Set when the dialog closes: connected, or cancelled with nothing saved.</summary>
    public ShellConnectionSetupResult? Result { get; private set; }

    /// <summary>For tests: the inputs, the buttons and the status line.</summary>
    internal (TextBox ServerUrl, TextBox Token, Button Toggle, Button Connect, Button Cancel, Label Status) PartsForTests =>
        (_serverUrl, _token, _toggleToken, _connect, _cancel, _status);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ShellWindowTheme.TrySetDarkTitleBar(Handle, _palette == ShellPalette.Dark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // A first setup starts at the address; a reconnect or change usually only needs a new token.
        if (_serverUrl.TextLength == 0)
            _serverUrl.Focus();
        else
            _token.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Once saving has begun the outcome must be reported, so a user close waits until it finishes. Closing
        // because the owner or the app goes away is never blocked; the engine abandons the change itself.
        if (_saving && e.CloseReason is CloseReason.UserClosing or CloseReason.None)
        {
            e.Cancel = true;
            return;
        }

        _verification?.Cancel();
        Result ??= _setup.Cancelled();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Nothing typed here outlives the window.
        _token.Clear();
        _verification?.Dispose();
        _verification = null;
        base.OnFormClosed(e);
    }

    internal async Task ConnectAsync()
    {
        if (_busy || IsDisposed)
            return;

        SetBusy(true, _localizer["Shell.Setup.Checking"]);
        _verification = new CancellationTokenSource();
        ShellConnectionSetupResult result;
        try
        {
            result = await _setup.ConnectAsync(
                _serverUrl.Text,
                _token.Text,
                _verification.Token,
                saving: () =>
                {
                    _saving = true;
                    ShowStatus(_localizer["Shell.Setup.Saving"], _palette.TextSecondary, announce: true);
                }).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // ShellConnectionSetup reports every expected failure as a result; this is a last resort.
            result = new ShellConnectionSetupResult(ShellConnectionSetupOutcome.VerificationFailed, _localizer["Shell.Op.Failed"]);
        }
        finally
        {
            _saving = false;
        }

        if (IsDisposed)
            return;

        if (result.IsConnected)
        {
            Result = result;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        if (result.Outcome == ShellConnectionSetupOutcome.Cancelled)
        {
            // Closed while checking; OnFormClosing already recorded the cancel.
            return;
        }

        SetBusy(false, null);
        ShowError(result);
    }

    private void CancelAndClose()
    {
        if (_saving)
            return;

        _verification?.Cancel();
        Result = _setup.Cancelled();
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void ToggleTokenVisibility()
    {
        _token.UseSystemPasswordChar = !_token.UseSystemPasswordChar;
        _toggleToken.Text = _localizer[_token.UseSystemPasswordChar ? "Button.ShowToken" : "Button.HideToken"];
        _toggleToken.AccessibleName = _toggleToken.Text;
        _token.Focus();
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        _serverUrl.ReadOnly = busy;
        _token.ReadOnly = busy;
        _connect.Inactive = busy;
        _toggleToken.Inactive = busy;
        UseWaitCursor = busy;
        if (message is not null)
            ShowStatus(message, _palette.TextSecondary, announce: true);
    }

    private void ShowError(ShellConnectionSetupResult result)
    {
        ShowStatus(result.Message, _palette.Danger, announce: true);
        var (frame, help) = result.Field switch
        {
            ShellConnectionSetupField.ServerUrl => (_serverUrlFrame, _serverUrlHelp),
            ShellConnectionSetupField.Token => (_tokenFrame, _tokenHelp),
            _ => ((ShellInputFrame?)null, (Label?)null)
        };

        if (frame is null)
        {
            _connect.Focus();
            return;
        }

        // The field is marked invalid and describes the error, so focus lands on what needs fixing.
        frame.Invalid = true;
        frame.Input.AccessibleDescription = result.Message + " " + help!.Text;
        frame.Input.Focus();
        frame.Input.SelectAll();
    }

    private void ClearFieldError(ShellInputFrame frame, Label help)
    {
        if (!frame.Invalid)
            return;

        frame.Invalid = false;
        frame.Input.AccessibleDescription = help.Text;
        if (!_busy)
            _status.Visible = false;
    }

    private void ShowStatus(string message, Color color, bool announce)
    {
        _status.Text = message;
        _status.ForeColor = color;
        _status.Visible = true;
        if (announce && IsHandleCreated)
        {
            _status.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.ImportantMostRecent,
                message);
        }
    }

    private Label Paragraph(string text, Color color, int bottom, bool small = false) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        ForeColor = color,
        Font = small ? ShellWindowTheme.BodyFont(9.5F) : ShellWindowTheme.BodyFont(),
        Margin = new Padding(0, 0, 0, bottom)
    };

    private void AddField(TableLayoutPanel layout, string label, Control input, Label help, TextBox? labelFor = null)
    {
        var target = labelFor ?? (input as ShellInputFrame)?.Input;
        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Font = ShellWindowTheme.StrongFont(9.5F),
            ForeColor = _palette.Text,
            Margin = new Padding(0, 0, 0, 2)
        });
        layout.Controls.Add(input);
        layout.Controls.Add(help);
        if (target is not null)
        {
            target.AccessibleName = label;
            target.AccessibleDescription = help.Text;
        }
    }

    private static string TitleKey(ShellConnectionSetupMode mode) => mode switch
    {
        ShellConnectionSetupMode.Reconnect => "Shell.Setup.Title.Reconnect",
        ShellConnectionSetupMode.Change => "Shell.Setup.Title.Change",
        _ => "Shell.Setup.Title.First"
    };

    private static string IntroKey(ShellConnectionSetupMode mode) => mode switch
    {
        ShellConnectionSetupMode.Reconnect => "Shell.Setup.Intro.Reconnect",
        ShellConnectionSetupMode.Change => "Shell.Setup.Intro.Change",
        _ => "Shell.Setup.Intro.First"
    };
}
