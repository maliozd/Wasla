using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// Window hosting the WebView2 Print Bridge app. It applies <see cref="ShellSecurityProfile"/>, maps only the
/// packaged asset folder to the synthetic shell origin, denies every navigation, popup, permission,
/// download and external request outside that origin, and forwards page messages to <see cref="ShellBridge"/>.
/// It also owns the privileged native actions the page may request (<see cref="IShellNativeActions"/>): the
/// connection dialog and the confirmations are host windows in the app's design, never page content.
/// Closing the window hides it to the tray, like the classic window.
/// </summary>
internal sealed class PrintBridgeShellForm : Form, IShellHost, IShellNativeActions
{
    private const int RefreshIntervalMilliseconds = 2000;

    private readonly WebView2 _webView;
    private readonly ShellBridge _bridge;
    private readonly ShellConnectionSetup _connectionSetup;
    private readonly PrintBridgeLocalizer _localizer;
    private readonly PrintBridgeCultureService _cultureService;
    private readonly ShellSecurityProfile _profile;
    private readonly ILogger _logger;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _useDarkPalette;
    private Form? _openDialog;
    private ShellConnectionDialog? _connectionDialog;
    private bool _connectionDialogSuperseded;

    private CoreWebView2Environment? _environment;
    private Task? _initialization;
    private bool _unavailableRaised;

    public PrintBridgeShellForm(
        IPrintBridgeEngine engine,
        PrintBridgeSettingsHolder settings,
        IPrinterCatalog printers,
        IShellPrinterSettings printerSettings,
        IShellOperationalSettings operationalSettings,
        ShellConnectionSetup connectionSetup,
        PrintBridgeLocalizer localizer,
        PrintBridgeCultureService cultureService,
        IShellLanguageSwitcher languageSwitcher,
        string? webView2Version,
        ILogger logger)
    {
        _connectionSetup = connectionSetup;
        _localizer = localizer;
        _cultureService = cultureService;
        _logger = logger;
        _profile = ShellSecurityProfile.ForCurrentBuild;
        _useDarkPalette = ShellWindowTheme.IsWindowsAppDarkModeEnabled();

        var canvas = ShellPalette.For(_useDarkPalette).Canvas;
        Text = _localizer["Common.AppTitle"];
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 580);
        MinimumSize = new Size(420, 420);
        BackColor = canvas;
        ShowInTaskbar = true;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = canvas,
            AllowExternalDrop = _profile.AllowExternalDrop
        };
        Controls.Add(_webView);

        var history = new ShellHistory(engine, localizer, cultureService);
        var operations = new ShellOperations(engine, printers, printerSettings, operationalSettings, this, history, settings, localizer, logger);
        _bridge = new ShellBridge(
            engine,
            new ShellSnapshotFactory(localizer, cultureService, settings, printers, () => operations.Busy, webView2Version),
            this,
            languageSwitcher,
            cultureService,
            operations,
            history,
            this,
            logger);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMilliseconds };
        _refreshTimer.Tick += (_, _) => _bridge.Refresh();

        _cultureService.CultureChanged += OnCultureChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Raised once when the shell cannot run; the tray then falls back to the classic window.</summary>
    public event EventHandler? ShellUnavailable;

    public event EventHandler? ClassicWindowRequested;

    /// <summary>
    /// True while the page can receive messages. A window hidden to the tray still receives them, so the result
    /// of an operation that finishes after the window was closed is never lost.
    /// </summary>
    public bool IsAvailable =>
        !IsDisposed && _webView.CoreWebView2 is not null && !_unavailableRaised;

    /// <summary>For the real-runtime integration tests only.</summary>
    internal CoreWebView2? CoreWebView2ForTests => _webView.CoreWebView2;

    internal ShellBridge BridgeForTests => _bridge;

    internal Task? InitializationForTests => _initialization;

    /// <summary>
    /// For tests: startup fails before any browser process is launched, through the same path as a broken runtime.
    /// (A real broken profile makes the browser show a blocking error dialog on the desktop.)
    /// </summary>
    internal bool FailStartupForTests { get; set; }

    /// <summary>
    /// Shows this window (there is only ever one) and brings it forward. <paramref name="tab"/> selects one of
    /// <see cref="ShellMessageContract.Tabs"/>; while the page is still loading the request waits for it.
    /// An open dialog of this window keeps the focus.
    /// </summary>
    public void ShowShell(string? tab = null)
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        _openDialog?.Activate();

        _initialization ??= InitializeAsync();
        _bridge.Resend();
        if (tab is not null)
            _bridge.Navigate(tab);
    }

    /// <summary>Starts the native connection dialog from the host (first run without a usable token).</summary>
    public void StartConnectionSetup() => _bridge.StartConnectionSetup();

    /// <summary>Shows the result of an automatic setup (setup link) in the page.</summary>
    public void ShowConnectionResult(ShellOperationOutcome outcome, string message, string? navigateTo = null) =>
        _bridge.NotifyConnectionResult(outcome, message, navigateTo);

    public void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed)
                    action();
            });
        }
        catch (InvalidOperationException)
        {
            // The window handle was destroyed between the check and the call; nothing to update.
        }
    }

    public void PostWebMessageAsJson(string json) => _webView.CoreWebView2?.PostWebMessageAsJson(json);

    public void OpenClassicWindow() => ClassicWindowRequested?.Invoke(this, EventArgs.Empty);

    public Task<ShellConnectionSetupResult> RunConnectionSetupAsync() =>
        ShowModalAsync(
            () =>
            {
                using var dialog = new ShellConnectionDialog(_connectionSetup, _localizer, _cultureService.IsRightToLeft, _useDarkPalette);
                _connectionDialog = dialog;
                _connectionDialogSuperseded = false;
                try
                {
                    var result = RunDialog(dialog, () => dialog.Result) ?? _connectionSetup.Cancelled();
                    // Closed for a setup link: the link reports the outcome, so this dialog reports nothing.
                    return _connectionDialogSuperseded && result.Outcome == ShellConnectionSetupOutcome.Cancelled
                        ? _connectionSetup.Superseded()
                        : result;
                }
                finally
                {
                    _connectionDialog = null;
                }
            },
            whenUnavailable: _connectionSetup.Cancelled());

    /// <summary>
    /// A setup link takes over from an open connection dialog (the first-run dialog, or one the page opened): the dialog
    /// closes without saving and reports nothing, so only the link's result is shown and the dialog cannot replace the
    /// connection the link sets up. A dialog that is already saving cannot be closed; it finishes and reports as usual.
    /// UI thread only.
    /// </summary>
    public void CloseConnectionDialogForSetupLink()
    {
        if (_connectionDialog is not { IsDisposed: false } dialog)
            return;

        _connectionDialogSuperseded = true;
        dialog.Close();
    }

    public Task<bool> ConfirmConnectionResetAsync() =>
        ShowModalAsync(
            () => Confirm(
                _localizer["Message.ResetConnectionConfirmTitle"],
                _localizer["Message.ResetConnectionConfirm"],
                _localizer["Button.ResetConnectionConfirm"],
                _localizer["Button.ResetConnectionCancel"]),
            whenUnavailable: false);

    public Task<bool> ConfirmEnableTestModeAsync() =>
        ShowModalAsync(
            () => Confirm(
                _localizer["Shell.TestMode.ConfirmTitle"],
                _localizer["Shell.TestMode.ConfirmMessage"],
                _localizer["Shell.TestMode.Confirm"],
                _localizer["Shell.Action.Cancel"]),
            whenUnavailable: false);

    public bool OpenLogFolder() => ShellLogFolder.TryOpen();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme();
    }

    /// <summary>
    /// Runs a modal dialog from a posted callback, never inside the WebView2 message handler that asked for it,
    /// and completes with <paramref name="whenUnavailable"/> when the window is gone before or while it runs.
    /// </summary>
    private Task<T> ShowModalAsync<T>(Func<T> show, T whenUnavailable)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated)
        {
            completion.SetResult(whenUnavailable);
            return completion.Task;
        }

        try
        {
            BeginInvoke(() =>
            {
                try
                {
                    completion.TrySetResult(IsDisposed ? whenUnavailable : show());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Print Bridge shell dialog could not be shown.");
                    completion.TrySetResult(whenUnavailable);
                }
            });
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(whenUnavailable);
        }

        return completion.Task;
    }

    private TResult RunDialog<TResult>(Form dialog, Func<TResult> result)
    {
        // A dialog needs its owner on screen; the page may have asked while the window was being hidden.
        if (!Visible)
            ShowShell();

        _openDialog = dialog;
        try
        {
            dialog.ShowDialog(this);
            return result();
        }
        finally
        {
            _openDialog = null;
        }
    }

    private bool Confirm(string title, string message, string confirmText, string cancelText)
    {
        if (!Visible)
            ShowShell();

        return ShellConfirmDialog.Ask(this, title, message, confirmText, cancelText, _cultureService.IsRightToLeft, _useDarkPalette);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // An open connection dialog closes as cancelled; nothing it held is saved.
            _openDialog?.Close();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _cultureService.CultureChanged -= OnCultureChanged;
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _bridge.Dispose();
            _webView.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (FailStartupForTests)
                throw new InvalidOperationException("Simulated WebView2 startup failure.");

            var options = new CoreWebView2EnvironmentOptions
            {
                AllowSingleSignOnUsingOSPrimaryAccount = false,
                AreBrowserExtensionsEnabled = false,
                Language = _cultureService.CurrentCulture.Name
            };
            _environment = await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: ShellPaths.UserDataDirectory, options)
                .ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);
            if (IsDisposed)
                return;

            var core = _webView.CoreWebView2;
            ApplySettings(core.Settings);
            AttachGuards(core);

            core.SetVirtualHostNameToFolderMapping(
                ShellNavigationPolicy.HostName,
                ShellPaths.AssetDirectory,
                CoreWebView2HostResourceAccessKind.Deny);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

            core.Navigate(ShellNavigationPolicy.StartUri.AbsoluteUri);
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            // Any startup failure (missing or broken runtime, locked profile folder, missing page files)
            // must leave the user with the classic window rather than an empty one.
            _logger.LogWarning(ex, "Print Bridge WebView2 shell could not start.");
            RaiseUnavailable();
        }
    }

    private void ApplySettings(CoreWebView2Settings settings)
    {
        settings.AreDevToolsEnabled = _profile.AreDevToolsEnabled;
        settings.AreBrowserAcceleratorKeysEnabled = _profile.AreBrowserAcceleratorKeysEnabled;
        settings.AreDefaultContextMenusEnabled = _profile.AreDefaultContextMenusEnabled;
        settings.AreHostObjectsAllowed = _profile.AreHostObjectsAllowed;
        settings.IsPasswordAutosaveEnabled = _profile.IsPasswordAutosaveEnabled;
        settings.IsGeneralAutofillEnabled = _profile.IsGeneralAutofillEnabled;
        settings.IsStatusBarEnabled = _profile.IsStatusBarEnabled;
        settings.AreDefaultScriptDialogsEnabled = _profile.AreDefaultScriptDialogsEnabled;
        settings.IsSwipeNavigationEnabled = _profile.IsSwipeNavigationEnabled;
        settings.IsWebMessageEnabled = _profile.IsWebMessageEnabled;
        settings.IsScriptEnabled = true;
        // Local files only: SmartScreen reputation lookups would be needless network traffic.
        settings.IsReputationCheckingRequired = false;
    }

    private void AttachGuards(CoreWebView2 core)
    {
        core.NavigationStarting += (_, e) =>
        {
            if (ShellNavigationPolicy.IsAllowedNavigation(e.Uri))
                return;

            e.Cancel = true;
            _logger.LogWarning("Print Bridge shell blocked a navigation outside the packaged page.");
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!ShellRequestPolicy.AllowFrameNavigation(e.Uri))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) =>
        {
            if (ShellRequestPolicy.AllowNewWindow(e.Uri))
                return;

            // Handled without a NewWindow means no popup and no external browser is opened.
            e.Handled = true;
            _logger.LogWarning("Print Bridge shell blocked a new-window request.");
        };
        core.PermissionRequested += (_, e) =>
        {
            if (ShellRequestPolicy.AllowPermission(e.PermissionKind.ToString()))
                return;

            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
        };
        core.DownloadStarting += (_, e) =>
        {
            if (ShellRequestPolicy.AllowDownload(e.DownloadOperation?.Uri))
                return;

            e.Cancel = true;
            e.Handled = true;
        };
        core.LaunchingExternalUriScheme += (_, e) =>
        {
            if (!ShellRequestPolicy.AllowExternalUriScheme(e.Uri))
                e.Cancel = true;
        };
        core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
        core.ServerCertificateErrorDetected += (_, e) =>
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        core.ContextMenuRequested += (_, e) =>
        {
            if (!_profile.AreDefaultContextMenusEnabled)
                e.Handled = true;
        };
        core.WebResourceRequested += (_, e) =>
        {
            if (ShellNavigationPolicy.IsAllowedResourceRequest(e.Request.Uri) || _environment is null)
                return;

            e.Response = _environment.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
        };
        core.WebMessageReceived += (_, e) =>
        {
            string? raw;
            try
            {
                raw = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                raw = null;
            }

            _bridge.HandleWebMessage(e.Source, raw);
        };
        core.NavigationCompleted += (_, e) =>
        {
            // A navigation this window cancelled (see NavigationStarting) also completes unsuccessfully.
            if (e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                return;

            _logger.LogWarning(
                "Print Bridge shell page failed to load. WebErrorStatus={WebErrorStatus}",
                e.WebErrorStatus);
            RaiseUnavailable();
        };
        core.ProcessFailed += (_, e) =>
        {
            _logger.LogWarning("Print Bridge shell WebView2 process failed. Kind={Kind}", e.ProcessFailedKind);
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                RaiseUnavailable();
            else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                     or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                core.Reload();
        };
    }

    private void OnCultureChanged(object? sender, EventArgs e) =>
        Post(() => Text = _localizer["Common.AppTitle"]);

    /// <summary>
    /// The app follows the Windows app theme (there is no separate Print Bridge theme setting). The page updates
    /// through <c>prefers-color-scheme</c>; this keeps the title bar and the pre-paint background in step.
    /// </summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
            return;

        Post(() =>
        {
            var dark = ShellWindowTheme.IsWindowsAppDarkModeEnabled();
            if (dark == _useDarkPalette)
                return;

            _useDarkPalette = dark;
            var canvas = ShellPalette.For(dark).Canvas;
            BackColor = canvas;
            _webView.DefaultBackgroundColor = canvas;
            ApplyTitleBarTheme();
        });
    }

    private void ApplyTitleBarTheme()
    {
        if (IsHandleCreated)
            ShellWindowTheme.TrySetDarkTitleBar(Handle, _useDarkPalette);
    }

    private void RaiseUnavailable()
    {
        if (_unavailableRaised)
            return;

        _unavailableRaised = true;
        _refreshTimer.Stop();
        Hide();
        ShellUnavailable?.Invoke(this, EventArgs.Empty);
    }
}
