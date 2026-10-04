using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// Window hosting the WebView2 status shell. It applies <see cref="ShellSecurityProfile"/>, maps only the
/// packaged asset folder to the synthetic shell origin, denies every navigation, popup, permission,
/// download and external request outside that origin, and forwards page messages to <see cref="ShellBridge"/>.
/// Closing the window hides it to the tray, like the classic window.
/// </summary>
internal sealed class PrintBridgeShellForm : Form, IShellHost
{
    private const int RefreshIntervalMilliseconds = 2000;

    private readonly WebView2 _webView;
    private readonly ShellBridge _bridge;
    private readonly PrintBridgeLocalizer _localizer;
    private readonly PrintBridgeCultureService _cultureService;
    private readonly ShellSecurityProfile _profile;
    private readonly ILogger _logger;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly bool _useDarkPalette;

    private CoreWebView2Environment? _environment;
    private Task? _initialization;
    private bool _unavailableRaised;

    public PrintBridgeShellForm(
        IPrintBridgeStatusSource statusSource,
        PrintBridgeLocalizer localizer,
        PrintBridgeCultureService cultureService,
        IShellLanguageSwitcher languageSwitcher,
        ILogger logger)
    {
        _localizer = localizer;
        _cultureService = cultureService;
        _logger = logger;
        _profile = ShellSecurityProfile.ForCurrentBuild;
        _useDarkPalette = IsWindowsAppDarkModeEnabled();

        var canvas = _useDarkPalette ? Color.FromArgb(0x1B, 0x18, 0x16) : Color.FromArgb(0xF7, 0xF4, 0xEE);
        Text = _localizer["Common.AppTitle"];
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 680);
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

        _bridge = new ShellBridge(
            statusSource,
            new ShellSnapshotFactory(localizer, cultureService),
            this,
            languageSwitcher,
            cultureService,
            logger);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMilliseconds };
        _refreshTimer.Tick += (_, _) => _bridge.Refresh();

        _cultureService.CultureChanged += OnCultureChanged;
    }

    /// <summary>Raised once when the shell cannot run; the tray then falls back to the classic window.</summary>
    public event EventHandler? ShellUnavailable;

    public event EventHandler? ClassicWindowRequested;

    public bool IsAvailable =>
        !IsDisposed && Visible && _webView.CoreWebView2 is not null && !_unavailableRaised;

    /// <summary>For the real-runtime integration tests only.</summary>
    internal CoreWebView2? CoreWebView2ForTests => _webView.CoreWebView2;

    internal ShellBridge BridgeForTests => _bridge;

    internal Task? InitializationForTests => _initialization;

    public void ShowShell()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();

        _initialization ??= InitializeAsync();
        _bridge.Resend();
    }

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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_useDarkPalette)
            TryUseDarkTitleBar(Handle);
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

    private void RaiseUnavailable()
    {
        if (_unavailableRaised)
            return;

        _unavailableRaised = true;
        _refreshTimer.Stop();
        Hide();
        ShellUnavailable?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsWindowsAppDarkModeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void TryUseDarkTitleBar(IntPtr handle)
    {
        const int DwmwaUseImmersiveDarkMode = 20;
        var enabled = 1;
        try
        {
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
