using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Core;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// Runs the real <see cref="PrintBridgeShellForm"/> with the installed WebView2 Runtime against a fake
/// engine. The window is placed off-screen and uses a WebView2 profile inside a temporary data root.
/// Skipped when no usable runtime is installed (WAS-55 owns runtime delivery).
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
[Trait("Category", "WebView2Runtime")]
public sealed class ShellWebViewRuntimeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wasla-pb-webview-tests", Guid.NewGuid().ToString("N"));
    private readonly IDisposable _rootScope;
    private readonly CultureScope _cultureScope = new();

    public ShellWebViewRuntimeTests()
    {
        Directory.CreateDirectory(_root);
        _rootScope = PrintBridgePaths.UseRootForTests(_root);
    }

    public void Dispose()
    {
        _rootScope.Dispose();
        _cultureScope.Dispose();
        for (var attempt = 0; attempt < 10 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The WebView2 browser process releases its profile shortly after the window closes.
                Thread.Sleep(300);
            }
        }
    }

    [Fact]
    public Task AppliesTheBuildSecurityProfileToTheRealWebView() =>
        RunShellAsync(async (_, core, _) =>
        {
            var expected = ShellSecurityProfile.ForCurrentBuild;
            var settings = core.Settings;

            Assert.Equal(expected.AreDevToolsEnabled, settings.AreDevToolsEnabled);
            Assert.Equal(expected.AreBrowserAcceleratorKeysEnabled, settings.AreBrowserAcceleratorKeysEnabled);
            Assert.False(settings.AreDefaultContextMenusEnabled);
            Assert.False(settings.AreHostObjectsAllowed);
            Assert.False(settings.IsPasswordAutosaveEnabled);
            Assert.False(settings.IsGeneralAutofillEnabled);
            Assert.False(settings.AreDefaultScriptDialogsEnabled);
            Assert.False(settings.IsStatusBarEnabled);
            Assert.False(settings.IsSwipeNavigationEnabled);
            Assert.True(settings.IsWebMessageEnabled);
#if !DEBUG
            Assert.False(settings.AreDevToolsEnabled);
#endif
            Assert.Equal(ShellNavigationPolicy.StartUri.AbsoluteUri, core.Source);
            Assert.StartsWith(_root, ShellPaths.UserDataDirectory, StringComparison.OrdinalIgnoreCase);
            await Task.CompletedTask;
        });

    [Fact]
    public Task RendersHostStateAndFollowsEngineChanges() =>
        RunShellAsync(async (form, core, source) =>
        {
            var localizer = new PrintBridgeLocalizer(CurrentCultureService!);

            Assert.Equal(localizer["Status.Online"], await EvalAsync(core, "document.getElementById('connection-label').textContent"));
            Assert.Equal("Kasa 1", await EvalAsync(core, "document.getElementById('device-name').textContent"));
            Assert.Equal("tr-TR", await EvalAsync(core, "document.documentElement.lang"));

            source.Set(Status(BridgeServerConnectionStatus.Error, new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServerUnreachable)));
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('connection').dataset.state") == "offline");

            Assert.Equal(localizer["Status.Offline"], await EvalAsync(core, "document.getElementById('connection-label').textContent"));
            Assert.Equal("false", await EvalAsync(core, "String(document.documentElement.outerHTML.includes('" + SentinelServerUrl + "'))"));
        });

    [Fact]
    public Task BlocksPopupsNavigationRemoteRequestsAndPermissions() =>
        RunShellAsync(async (_, core, _) =>
        {
            var popupHandled = new List<bool>();
            core.NewWindowRequested += (_, e) => popupHandled.Add(e.Handled);

            Assert.Equal("null", await EvalAsync(core, "String(window.open('https://example.com/', '_blank'))"));
            Assert.Equal([true], popupHandled);

            Assert.StartsWith("blocked", await EvalAsync(core, "fetch('https://example.com/').then(() => 'loaded', e => 'blocked ' + e.name)"));
            Assert.Equal("blocked", await EvalAsync(core,
                "new Promise(r => { const i = new Image(); i.onload = () => r('loaded'); i.onerror = () => r('blocked'); i.src = 'https://example.com/p.png'; })"));
            Assert.Equal("denied", await EvalAsync(core, "Notification.requestPermission()"));
            Assert.Equal("denied", await EvalAsync(core,
                "new Promise(r => navigator.geolocation.getCurrentPosition(() => r('granted'), () => r('denied')))"));
            // With allowUnsafeEvalBlockedByCSP = false the page CSP governs every form of dynamic code.
            Assert.StartsWith("blocked", await EvalAsync(core, "(() => { try { return String(eval('1+1')); } catch (e) { return 'blocked ' + e.name; } })()"));
            Assert.StartsWith("blocked", await EvalAsync(core, "(() => { try { return String(new Function('return 2')()); } catch (e) { return 'blocked ' + e.name; } })()"));
            Assert.StartsWith("blocked", await EvalAsync(core, "(() => { try { document.body.insertAdjacentHTML('beforeend', '<b>x</b>'); return 'inserted'; } catch (e) { return 'blocked ' + e.name; } })()"));

            await EvalAsync(core, "location.href = 'https://example.com/'; 'navigating'");
            await EvalAsync(core, "new Promise(r => setTimeout(() => r('waited'), 1500))");
            Assert.Equal(ShellNavigationPolicy.StartUri.AbsoluteUri, core.Source);

            await EvalAsync(core, "location.href = 'file:///C:/Windows/win.ini'; 'navigating'");
            await EvalAsync(core, "new Promise(r => setTimeout(() => r('waited'), 1500))");
            Assert.Equal(ShellNavigationPolicy.StartUri.AbsoluteUri, core.Source);
        });

    [Fact]
    public Task AcceptsOnlyContractMessagesFromThePage() =>
        RunShellAsync(async (form, core, _) =>
        {
            var classicRequests = 0;
            form.ClassicWindowRequested += (_, _) => classicRequests++;
            var sentBefore = form.BridgeForTests.SentSnapshotCount;

            await EvalAsync(core, """
                chrome.webview.postMessage('{"version":1,"type":"classicWindow.open","payload":{"tab":"settings"}}');
                chrome.webview.postMessage('{"version":1,"type":"host.exec","payload":{"method":"Exit"}}');
                chrome.webview.postMessage({ version: 1, type: 'classicWindow.open' });
                chrome.webview.postMessage('x'.repeat(5000));
                'sent'
                """);
            await EvalAsync(core, "new Promise(r => setTimeout(() => r('waited'), 500))");
            Assert.Equal(0, classicRequests);

            await EvalAsync(core, """chrome.webview.postMessage('{"version":1,"type":"classicWindow.open","payload":{}}'); 'sent'""");
            await WaitUntilAsync(() => Task.FromResult(classicRequests == 1));

            await EvalAsync(core, """chrome.webview.postMessage('{"version":1,"type":"ui.ready","payload":{}}'); 'sent'""");
            await WaitUntilAsync(() => Task.FromResult(form.BridgeForTests.SentSnapshotCount > sentBefore));
            Assert.Equal("0", await EvalAsync(core, "String(localStorage.length + sessionStorage.length + document.cookie.length)"));
        });

    [ThreadStatic]
    private static PrintBridgeCultureService? CurrentCultureService;

    private Task RunShellAsync(Func<PrintBridgeShellForm, CoreWebView2, FakeStatusSource, Task> body)
    {
        if (!new WebView2RuntimeProbe().Probe().IsAvailable)
            Assert.Skip("No usable WebView2 Runtime is installed on this machine.");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            var loop = new ApplicationContext();
            context.Post(async _ =>
            {
                try
                {
                    var culture = new PrintBridgeCultureService();
                    culture.Initialize(SupportedCultures.Turkish);
                    CurrentCultureService = culture;
                    var source = new FakeStatusSource();
                    using var form = new PrintBridgeShellForm(
                        source,
                        new PrintBridgeLocalizer(culture),
                        culture,
                        new FakeLanguageSwitcher(culture),
                        NullLogger.Instance)
                    {
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(-32000, -32000),
                        ShowInTaskbar = false
                    };
                    var unavailable = false;
                    form.ShellUnavailable += (_, _) => unavailable = true;

                    form.ShowShell();
                    await form.InitializationForTests!.WaitAsync(Timeout);
                    Assert.False(unavailable, "The shell reported that it could not start.");
                    var core = form.CoreWebView2ForTests ?? throw new InvalidOperationException("WebView2 did not initialize.");
                    await WaitUntilAsync(() => Task.FromResult(form.BridgeForTests.SentSnapshotCount >= 1));

                    await body(form, core, source);
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    loop.ExitThread();
                }
            }, null);
            System.Windows.Forms.Application.Run(loop);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return completion.Task.WaitAsync(Timeout + Timeout);
    }

    private static async Task<string?> EvalAsync(CoreWebView2 core, string expression)
    {
        // DevTools evaluations bypass the page CSP for eval by default; opt out so the page policy applies.
        var request = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true, allowUnsafeEvalBlockedByCSP = false });
        var response = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request);
        using var document = JsonDocument.Parse(response);
        var result = document.RootElement.GetProperty("result");
        return result.TryGetProperty("value", out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not reached in time.");
            await Task.Delay(50);
        }
    }
}
