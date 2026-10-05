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
            Assert.Equal("false", await EvalAsync(core, "String(document.documentElement.outerHTML.includes('" + SentinelToken + "'))"));
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
                chrome.webview.postMessage('{"version":2,"type":"classicWindow.open","payload":{"tab":"settings"}}');
                chrome.webview.postMessage('{"version":2,"type":"host.exec","payload":{"method":"Exit"}}');
                chrome.webview.postMessage({ version: 2, type: 'classicWindow.open' });
                chrome.webview.postMessage('x'.repeat(5000));
                'sent'
                """);
            await EvalAsync(core, "new Promise(r => setTimeout(() => r('waited'), 500))");
            Assert.Equal(0, classicRequests);

            await EvalAsync(core, """chrome.webview.postMessage('{"version":2,"type":"classicWindow.open","payload":{}}'); 'sent'""");
            await WaitUntilAsync(() => Task.FromResult(classicRequests == 1));

            await EvalAsync(core, """chrome.webview.postMessage('{"version":2,"type":"ui.ready","payload":{}}'); 'sent'""");
            await WaitUntilAsync(() => Task.FromResult(form.BridgeForTests.SentSnapshotCount > sentBefore));
            Assert.Equal("0", await EvalAsync(core, "String(localStorage.length + sessionStorage.length + document.cookie.length)"));
        });

    [Fact]
    public Task TabsFollowTheKeyboard_InBothReadingDirections() =>
        RunShellAsync(async (_, core, _) =>
        {
            await EvalAsync(core, "document.getElementById('tab-overview').focus(); 'focused'");

            await PressAsync(core, "ArrowRight");
            Assert.Equal("""["tab-printer","true",false,true,-1]""", await EvalAsync(core, TabStateScript("printer", "overview")));

            await PressAsync(core, "End");
            Assert.Equal("""["tab-settings","true",false,true,-1]""", await EvalAsync(core, TabStateScript("settings", "printer")));

            await EvalAsync(core, "const s = document.getElementById('language'); s.value = 'ar-SA'; s.dispatchEvent(new Event('change')); 'changed'");
            await WaitUntilAsync(async () => await EvalAsync(core, "document.documentElement.dir") == "rtl");
            Assert.Equal("ar-SA", await EvalAsync(core, "document.documentElement.lang"));

            // In right-to-left the left arrow moves forward: Settings wraps to Overview.
            await EvalAsync(core, "document.getElementById('tab-settings').focus(); 'focused'");
            await PressAsync(core, "ArrowLeft");
            Assert.Equal("""["tab-overview","true",false,true,-1]""", await EvalAsync(core, TabStateScript("overview", "settings")));
            Assert.Contains("2026", await EvalAsync(core, "document.getElementById('last-contact').textContent"));
        });

    [Fact]
    public Task TestPrintFromThePage_RunsOnceWhileBusy_AndShowsTheHostResult() =>
        RunShellAsync(async (_, core, source) =>
        {
            var localizer = new PrintBridgeLocalizer(CurrentCultureService!);
            source.TestPrintGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await EvalAsync(core, "document.getElementById('action-test-print').click(); 'clicked'");
            await WaitUntilAsync(() => Task.FromResult(source.TestPrintCalls == 1));
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('action-test-print').getAttribute('aria-busy')") == "true");
            Assert.Equal("busy-hint", await EvalAsync(core, "document.getElementById('action-test-print').getAttribute('aria-describedby')"));

            await EvalAsync(core, "document.getElementById('action-test-print').click(); document.getElementById('printer-test').click(); 'clicked'");
            await EvalAsync(core, "new Promise(r => setTimeout(() => r('waited'), 300))");
            Assert.Equal(1, source.TestPrintCalls);

            source.TestPrintGate.SetResult();
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('toast').hidden") == "false");
            Assert.Equal(localizer["Message.TestPrintSent"], await EvalAsync(core, "document.getElementById('toast-text').textContent"));
            Assert.Equal(localizer["Message.TestPrintSent"], await EvalAsync(core, "document.getElementById('announcer').textContent"));
            await WaitUntilAsync(async () => await EvalAsync(core, "String(document.getElementById('action-test-print').getAttribute('aria-busy'))") == "null");
            Assert.Equal(1, source.TestPrintCalls);
        });

    [Fact]
    public Task EngineToggle_StopsAndStartsThroughTheHost() =>
        RunShellAsync(async (_, core, source) =>
        {
            var localizer = new PrintBridgeLocalizer(CurrentCultureService!);
            Assert.Equal(localizer["Button.StopListening"], await EvalAsync(core, "document.getElementById('action-engine').textContent"));

            await EvalAsync(core, "document.getElementById('action-engine').click(); 'clicked'");
            await WaitUntilAsync(() => Task.FromResult(source.StopCalls == 1));
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('action-engine').textContent") == localizer["Button.StartListening"]);
            Assert.Equal("stopped", await EvalAsync(core, "document.getElementById('connection').dataset.state"));

            await EvalAsync(core, "document.getElementById('action-engine').click(); 'clicked'");
            await WaitUntilAsync(() => Task.FromResult(source.StartCalls == 1));
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('action-engine').textContent") == localizer["Button.StopListening"]);
            Assert.Equal(1, source.StopCalls);
        });

    [Fact]
    public Task Reprint_NeedsAnExplicitConfirmationInThePage() =>
        RunShellAsync(async (_, core, source) =>
        {
            var jobId = Guid.NewGuid();
            source.History = [new LocalPrintJobRecord { JobId = jobId, OrderDisplay = "GTR-1001", Status = LocalPrintJobStatus.Printed, CreatedAtUtc = DateTime.UtcNow, PrintedAtUtc = DateTime.UtcNow }];

            await EvalAsync(core, "document.getElementById('tab-history').click(); 'opened'");
            await WaitUntilAsync(async () => await EvalAsync(core, "String(document.querySelectorAll('#history-rows [data-role=\"reprint\"]').length)") == "1");

            await EvalAsync(core, "document.querySelector('#history-rows [data-role=\"reprint\"]').click(); 'asked'");
            await WaitUntilAsync(async () => await EvalAsync(core, "String(document.activeElement.getAttribute('data-role'))") == "confirm");
            Assert.Equal(0, source.ReprintCalls);

            await EvalAsync(core, "document.querySelector('#history-rows [data-role=\"cancel\"]').click(); 'cancelled'");
            Assert.Equal("reprint", await EvalAsync(core, "String(document.activeElement.getAttribute('data-role'))"));
            Assert.Equal(0, source.ReprintCalls);

            await EvalAsync(core, "document.querySelector('#history-rows [data-role=\"reprint\"]').click(); 'asked'");
            await EvalAsync(core, "document.querySelector('#history-rows [data-role=\"confirm\"]').click(); 'confirmed'");
            await WaitUntilAsync(() => Task.FromResult(source.ReprintCalls == 1));

            Assert.Equal([jobId], source.ReprintedJobs);
            var html = await EvalAsync(core, "document.documentElement.outerHTML");
            Assert.DoesNotContain(jobId.ToString("D"), html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(jobId.ToString("N"), html, StringComparison.OrdinalIgnoreCase);
        });

    [Fact]
    public Task FollowsTheWindowsLightOrDarkPreference() =>
        RunShellAsync(async (_, core, _) =>
        {
            await EmulateAsync(core, "prefers-color-scheme", "light");
            var light = await EvalAsync(core, "getComputedStyle(document.body).backgroundColor + '|' + getComputedStyle(document.body).color");
            await EmulateAsync(core, "prefers-color-scheme", "dark");
            var dark = await EvalAsync(core, "getComputedStyle(document.body).backgroundColor + '|' + getComputedStyle(document.body).color");

            Assert.NotEqual(light, dark);
            Assert.Equal("light dark", await EvalAsync(core, "document.querySelector('meta[name=\"color-scheme\"]').content"));
        });

    [Fact]
    public Task MinimumWindow_And200PercentZoom_NeedNoHorizontalScrolling() =>
        RunShellAsync(async (form, core, _) =>
        {
            form.Size = form.MinimumSize;
            await WaitUntilAsync(async () => int.Parse(await EvalAsync(core, "String(document.documentElement.clientWidth)") ?? "0") < 440);
            await AssertNoHorizontalOverflowOnEveryTabAsync(core);

            // 200 % zoom of the default window, and the 320 CSS px reflow width.
            foreach (var width in new[] { 380, 320 })
            {
                await core.CallDevToolsProtocolMethodAsync(
                    "Emulation.setDeviceMetricsOverride",
                    JsonSerializer.Serialize(new { width, height = 290, deviceScaleFactor = 2, mobile = false }));
                await WaitUntilAsync(async () => await EvalAsync(core, "String(document.documentElement.clientWidth)") == width.ToString());
                await AssertNoHorizontalOverflowOnEveryTabAsync(core);
            }

            await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
        });

    [Fact]
    public Task ConnectionSetup_OpensTheNativeWindow_AndThePageHasNoCredentialFields() =>
        RunShellAsync(async (form, core, _) =>
        {
            var setupRequests = 0;
            form.ConnectionSetupRequested += (_, _) => setupRequests++;

            await EvalAsync(core, "document.getElementById('tab-settings').click(); document.getElementById('settings-open-setup').click(); 'opened'");
            await WaitUntilAsync(() => Task.FromResult(setupRequests == 1));

            Assert.Equal("0", await EvalAsync(core, "String(document.querySelectorAll('input[type=\"password\"], input[type=\"url\"], textarea, form').length)"));
            var html = await EvalAsync(core, "document.documentElement.outerHTML");
            Assert.DoesNotContain(SentinelToken, html, StringComparison.Ordinal);
            Assert.DoesNotContain("SENTINEL", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SentinelServerUrl, html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.MachineName, html, StringComparison.OrdinalIgnoreCase);
        });

    private static string TabStateScript(string selected, string previous) =>
        $"JSON.stringify([document.activeElement.id, document.getElementById('tab-{selected}').getAttribute('aria-selected'), " +
        $"document.getElementById('panel-{selected}').hidden, document.getElementById('panel-{previous}').hidden, " +
        $"document.getElementById('tab-{previous}').tabIndex])";

    private static async Task AssertNoHorizontalOverflowOnEveryTabAsync(CoreWebView2 core)
    {
        foreach (var tab in new[] { "overview", "printer", "history", "settings" })
        {
            await EvalAsync(core, $"document.getElementById('tab-{tab}').click(); 'shown'");
            // Content may wrap but never overflow the viewport. The tab strip and the history table scroll
            // inside their own containers, which reflow rules allow for two-dimensional data.
            var offenders = await EvalAsync(core, """
                (() => {
                  const width = document.documentElement.clientWidth;
                  const out = [];
                  if (document.documentElement.scrollWidth > width) out.push('document');
                  for (const el of document.querySelectorAll('#app *')) {
                    if (el.closest('[hidden], .pb-visually-hidden, .pb-table-wrap, .pb-tabs')) continue;
                    const r = el.getBoundingClientRect();
                    if (r.width === 0 || r.height === 0) continue;
                    if (r.right > width + 1 || r.left < -1) out.push(el.id || el.className || el.tagName);
                  }
                  return JSON.stringify(out.slice(0, 8));
                })()
                """);
            Assert.True(offenders == "[]", $"{tab}: {offenders}");
        }
    }

    /// <summary>A real key press through the browser input pipeline, not a synthetic DOM event.</summary>
    private static async Task PressAsync(CoreWebView2 core, string key)
    {
        foreach (var type in new[] { "keyDown", "keyUp" })
        {
            await core.CallDevToolsProtocolMethodAsync(
                "Input.dispatchKeyEvent",
                JsonSerializer.Serialize(new { type, key, code = key, windowsVirtualKeyCode = VirtualKey(key) }));
        }
    }

    private static int VirtualKey(string key) => key switch
    {
        "ArrowLeft" => 0x25,
        "ArrowRight" => 0x27,
        "Home" => 0x24,
        "End" => 0x23,
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    private static Task EmulateAsync(CoreWebView2 core, string feature, string value) =>
        core.CallDevToolsProtocolMethodAsync(
            "Emulation.setEmulatedMedia",
            JsonSerializer.Serialize(new { features = new[] { new { name = feature, value } } }));

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
                        ShellTestRig.NewSettings(SentinelToken),
                        new FakePrinterCatalog(),
                        new FakePrinterSettings(),
                        new PrintBridgeLocalizer(culture),
                        culture,
                        new FakeLanguageSwitcher(culture),
                        "154.0.4258.53",
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
