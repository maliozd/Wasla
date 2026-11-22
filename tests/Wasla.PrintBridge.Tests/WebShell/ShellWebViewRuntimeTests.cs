using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Core;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Updates;
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

    // Anything the window still saves while closing lands here; the data root waits for the UI thread.
    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-webview-tests");
    private readonly CultureScope _cultureScope = new();

    public void Dispose()
    {
        _dataRoot.Dispose();
        _cultureScope.Dispose();
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
            Assert.StartsWith(_dataRoot.Path, ShellPaths.UserDataDirectory, StringComparison.OrdinalIgnoreCase);
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
            var culture = CurrentCultureService!;
            var sentBefore = form.BridgeForTests.SentSnapshotCount;

            // An unknown field, a non-string message, an unknown type and an oversized message change nothing.
            await EvalAsync(core, """
                chrome.webview.postMessage('{"version":4,"type":"language.change","payload":{"culture":"en-US","tab":"settings"}}');
                chrome.webview.postMessage('{"version":4,"type":"host.exec","payload":{"method":"Exit"}}');
                chrome.webview.postMessage({ version: 4, type: 'language.change', payload: { culture: 'en-US' } });
                chrome.webview.postMessage('x'.repeat(5000));
                'sent'
                """);
            await EvalAsync(core, "new Promise(r => setTimeout(() => r('waited'), 500))");
            Assert.Equal(SupportedCultures.Turkish, culture.CurrentCulture.Name);

            await EvalAsync(core, """chrome.webview.postMessage('{"version":4,"type":"language.change","payload":{"culture":"en-US"}}'); 'sent'""");
            await WaitUntilAsync(() => Task.FromResult(culture.CurrentCulture.Name == "en-US"));

            await EvalAsync(core, """chrome.webview.postMessage('{"version":4,"type":"ui.ready","payload":{}}'); 'sent'""");
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

    [Theory]
    [InlineData("tr-TR", "ltr")]
    [InlineData("en-US", "ltr")]
    [InlineData("ar-SA", "rtl")]
    [InlineData("ru-RU", "ltr")]
    public Task MinimumWindow_And200PercentZoom_NeedNoHorizontalScrolling(string culture, string direction) =>
        RunShellAsync(async (form, core, source) =>
        {
            var localizer = new PrintBridgeLocalizer(CurrentCultureService!);
            await EvalAsync(core, $"(() => {{ const s = document.getElementById('language'); s.value = '{culture}'; s.dispatchEvent(new Event('change')); return 'changed'; }})()");
            await WaitUntilAsync(async () => await EvalAsync(core, "document.documentElement.lang + ' ' + document.documentElement.dir") == $"{culture} {direction}");

            form.Size = form.MinimumSize;
            await WaitUntilAsync(async () => int.Parse(await EvalAsync(core, "String(document.documentElement.clientWidth)") ?? "0") < 440);
            await AssertNoHorizontalOverflowOnEveryTabAsync(core, culture);

            // 200 % zoom of the default window, and the 320 CSS px reflow width.
            foreach (var width in new[] { 380, 320 })
            {
                await core.CallDevToolsProtocolMethodAsync(
                    "Emulation.setDeviceMetricsOverride",
                    JsonSerializer.Serialize(new { width, height = 290, deviceScaleFactor = 2, mobile = false }));
                await WaitUntilAsync(async () => await EvalAsync(core, "String(document.documentElement.clientWidth)") == width.ToString());
                await AssertNoHorizontalOverflowOnEveryTabAsync(core, $"{culture} {width}px");
            }

            // The status pill is the header text whose width changes, so every connection state is checked at 320 CSS px.
            // Its label is shown in full; only a blocking-issue title longer than the pill's cap ends in an ellipsis.
            foreach (var (titleKey, status, fullLabel) in HeaderStatuses)
            {
                var label = localizer[titleKey];
                source.Set(status);
                await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('status-pill-label').textContent") == label);
                var offenders = await HorizontalOverflowOffendersAsync(core, fullLabel);
                Assert.True(offenders == "[]", $"{culture} 320px, {titleKey} \"{label}\": {offenders}");
            }

            await AssertUpdatesFitInEveryStateAsync(core, localizer, $"{culture} 320px");
            await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
        });

    /// <summary>
    /// WAS-63: Settings → Updates in every state the host shows, as it shows them: before a check, on an unsigned
    /// installation, up to date, available, each step of an attempt (downloading with its progress bar, verifying, waiting
    /// for printing, ready, installing), each result (installed, not installed, cancelled and every failure text), and this
    /// app's own attempt (opening the setup, the setup open, the setup closed, a setup that could not be opened). In each,
    /// nothing scrolls the page sideways, leaves the window, the content or the card, is cut, or overlaps.
    /// </summary>
    private static async Task AssertUpdatesFitInEveryStateAsync(CoreWebView2 core, PrintBridgeLocalizer localizer, string context)
    {
        var updates = CurrentUpdates!;
        var launcher = CurrentLauncher!;
        var verifier = CurrentVerifier!;
        var newer = FakeUpdates.Newer;
        await EvalAsync(core, "document.getElementById('tab-settings').click(); 'shown'");

        async Task FitsAsync(string what, string stage, string label)
        {
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('update-card').dataset.stage + '|' + document.getElementById('update-status').textContent") == $"{stage}|{label}");
            var page = await HorizontalOverflowOffendersAsync(core, fullLabel: true);
            var card = await EvalAsync(core, UpdateCardOffendersScript);
            Assert.True(page == "[]" && card == "[]", $"{context}, Updates {what} \"{label}\": page {page}, card {card}");
        }

        async Task CheckAsync(string? available)
        {
            updates.Available = available;
            await EvalAsync(core, "document.getElementById('update-check').click(); 'checked'");
        }

        // The user confirms the update in the native dialog; the service prepares it and reports the verified setup ready.
        async Task ConfirmAsync()
        {
            updates.NextAttempt = Guid.NewGuid();
            var before = Prepared();
            await EvalAsync(core, "document.getElementById('update-start').click(); 'asked'");
            var dialog = await WaitForOpenFormAsync<ShellConfirmDialog>();
            dialog.Confirm.PerformClick();
            await WaitUntilAsync(() => Task.FromResult(Prepared() == before + 1));
            updates.Current = FakeUpdates.Attempt(updates.NextAttempt, PrintBridgeUpdateStage.Ready);
        }

        int Prepared()
        {
            lock (updates.Prepared)
                return updates.Prepared.Count;
        }

        await FitsAsync("before a check", ShellUpdateStages.Idle, string.Empty);
        updates.Current = FakeUpdates.Idle(supported: false);
        await FitsAsync("on an unsigned installation", ShellUpdateStages.Unsupported, localizer["Shell.Update.Status.Unsupported"]);
        updates.Current = FakeUpdates.Idle();
        await FitsAsync("before a check", ShellUpdateStages.Idle, string.Empty);
        await CheckAsync(null);
        await FitsAsync("up to date", ShellUpdateStages.UpToDate, localizer["Shell.Update.Status.UpToDate"]);
        await CheckAsync(newer);
        await FitsAsync("available", ShellUpdateStages.Available, localizer.GetString("Shell.Update.Status.Available", newer));

        // An attempt as the service reports it (one this window did not start).
        foreach (var (what, update, stage, label) in new (string, Wasla.PrintBridge.Ipc.IpcUpdate, string, string)[]
                 {
                     ("downloading", FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Downloading, percent: 40), ShellUpdateStages.Downloading, localizer.GetString("Shell.Update.Status.Downloading", newer)),
                     ("verifying", FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Verifying), ShellUpdateStages.Verifying, localizer["Shell.Update.Status.Verifying"]),
                     ("waiting for printing", FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.WaitingForPrinting), ShellUpdateStages.WaitingForPrinting, localizer["Shell.Update.Status.WaitingForPrinting"]),
                     ("ready", FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Ready), ShellUpdateStages.Ready, localizer.GetString("Shell.Update.Status.Ready", newer)),
                     ("installing", FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Ready, stagedPath: null), ShellUpdateStages.Installing, localizer.GetString("Shell.Update.Status.Installing", newer)),
                     ("installed", FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Succeeded), ShellUpdateStages.Succeeded, localizer.GetString("Shell.Update.Status.Succeeded", newer)),
                     ("not installed", FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.NotApplied), ShellUpdateStages.NotApplied, localizer.GetString("Shell.Update.Status.NotApplied", FakeUpdates.Installed)),
                     ("cancelled", FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Cancelled), ShellUpdateStages.Cancelled, localizer["Shell.Update.Status.Cancelled"])
                 })
        {
            updates.Current = update;
            await FitsAsync(what, stage, label);
            Assert.Equal(stage == ShellUpdateStages.Downloading ? "false" : "true", await EvalAsync(core, "String(document.getElementById('update-progress-row').hidden)"));
        }

        foreach (var failure in Enum.GetValues<PrintBridgeUpdateFailure>())
        {
            updates.Current = FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Failed, failure);
            await FitsAsync($"failed ({failure})", ShellUpdateStages.Failed, localizer[ShellUpdates.FailureKey(ShellUpdates.FailureGroup(failure))]);
        }

        // This window's own attempt: the app checks the setup (held), opens it, and the setup closes without installing;
        // then a setup that cannot be opened. The fake service records each cancel, as the service does.
        updates.OnCancel = id => updates.Current = FakeUpdates.Attempt(id, PrintBridgeUpdateStage.Cancelled);
        using (var held = new ManualResetEventSlim(false))
        {
            verifier.Gate = held;
            updates.Current = FakeUpdates.Idle();
            await FitsAsync("available again", ShellUpdateStages.Available, localizer.GetString("Shell.Update.Status.Available", newer));
            await ConfirmAsync();
            await FitsAsync("opening its setup", ShellUpdateStages.Opening, localizer["Shell.Update.Status.Opening"]);
            held.Set();
            await FitsAsync("with its setup open", ShellUpdateStages.SetupOpen, localizer["Shell.Update.Status.SetupOpen"]);
            launcher.Exit();
            await FitsAsync("after its setup closed", ShellUpdateStages.NotApplied, localizer.GetString("Shell.Update.Status.NotApplied", FakeUpdates.Installed));
            verifier.Gate = null;
        }

        launcher.Failure = new System.ComponentModel.Win32Exception(2);
        updates.Current = FakeUpdates.Idle();
        await FitsAsync("available again", ShellUpdateStages.Available, localizer.GetString("Shell.Update.Status.Available", newer));
        await ConfirmAsync();
        await FitsAsync("when its setup could not be opened", ShellUpdateStages.Failed, localizer["Shell.Update.Failure.Setup"]);
    }

    /// <summary>
    /// The Updates card at the current width: the content area does not scroll sideways, the card stays inside it, every
    /// visible element stays inside the card and shows all of its content (no word or button label wider than its box),
    /// and the card's parts, its buttons and the progress bar and its percentage do not overlap.
    /// </summary>
    private const string UpdateCardOffendersScript = """
        (() => {
          const card = document.getElementById('update-card');
          const main = document.querySelector('.pb-main');
          const box = el => el.getBoundingClientRect();
          const name = el => el.id || el.className || el.tagName;
          const visible = el => !el.closest('[hidden]') && box(el).width > 0 && box(el).height > 0;
          const out = [];
          if (!visible(card)) return JSON.stringify(['card not shown']);
          if (main.scrollWidth > main.clientWidth) out.push('content scrolls sideways');
          const area = box(main), c = box(card);
          if (c.left < area.left - 1 || c.right > area.right + 1) out.push('card outside the content');
          for (const el of card.querySelectorAll('*')) {
            if (!visible(el)) continue;
            const r = box(el);
            if (r.left < c.left - 1 || r.right > c.right + 1) out.push('outside the card: ' + name(el));
            if (el.clientWidth > 0 && el.scrollWidth > el.clientWidth + 1) out.push('cut: ' + name(el));
          }
          const overlaps = list => list.forEach((a, i) => list.slice(i + 1).forEach(b => {
            const p = box(a), q = box(b);
            if (p.left < q.right - 0.5 && q.left < p.right - 0.5 && p.top < q.bottom - 0.5 && q.top < p.bottom - 0.5) out.push('overlap ' + name(a) + ' / ' + name(b));
          }));
          overlaps([...card.children].filter(visible));
          overlaps([...document.querySelectorAll('#update-actions > button')].filter(visible));
          overlaps([...document.querySelectorAll('#update-progress-row > *')].filter(visible));
          return JSON.stringify(out.slice(0, 8));
        })()
        """;

    [Fact]
    public Task ConnectionSetup_OpensTheNativeDialog_AndThePageNeverSeesTheSecrets() =>
        RunShellAsync(async (form, core, source) =>
        {
            var localizer = new PrintBridgeLocalizer(CurrentCultureService!);
            // The real engine records the verified contact and raises StatusChanged; the fake does the same.
            source.OnApplied = () => source.Set(Status(displayName: "Kasa 9"));
            source.Set(Status(BridgeServerConnectionStatus.Error, new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServerUnreachable), isRunning: false));
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('connection').dataset.state") == "offline");

            await EvalAsync(core, "document.getElementById('tab-settings').click(); document.getElementById('settings-open-setup').click(); 'opened'");
            var dialog = await WaitForOpenFormAsync<ShellConnectionDialog>();

            // The page shows host busy state while the dialog is open, and has no credential fields of its own.
            await WaitUntilAsync(async () => await EvalAsync(core, "String(document.getElementById('settings-open-setup').getAttribute('aria-busy'))") == "true");
            Assert.Equal("0", await EvalAsync(core, "String(document.querySelectorAll('input[type=\"password\"], input[type=\"url\"], textarea, form').length)"));
            Assert.Same(form, dialog.Owner);
            Assert.Equal(string.Empty, dialog.PartsForTests.Token.Text);
            Assert.True(dialog.PartsForTests.Token.UseSystemPasswordChar);

            dialog.PartsForTests.ServerUrl.Text = "https://print-bridge.test";
            dialog.PartsForTests.Token.Text = NewTypedToken;
            await dialog.ConnectAsync();

            // The result and the fresh state arrive without a reload.
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('toast-text').textContent") == localizer["Shell.Setup.Connected"]);
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('connection').dataset.state") == "online");
            Assert.Equal("Kasa 9", await EvalAsync(core, "document.getElementById('device-name').textContent"));
            Assert.Equal(("https://print-bridge.test", NewTypedToken, false), source.AppliedConnection);

            var html = await EvalAsync(core, "document.documentElement.outerHTML");
            foreach (var secret in new[] { SentinelToken, "SENTINEL", SentinelServerUrl, NewTypedToken, "https://print-bridge.test", Environment.MachineName })
                Assert.DoesNotContain(secret, html, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("0", await EvalAsync(core, "String(localStorage.length + sessionStorage.length + document.cookie.length)"));
        });

    [Fact]
    public Task ConnectionSetup_Cancelled_ChangesNothing_AndSaysSo() =>
        RunShellAsync(async (_, core, source) =>
        {
            var localizer = new PrintBridgeLocalizer(CurrentCultureService!);

            await EvalAsync(core, "document.getElementById('tab-settings').click(); document.getElementById('settings-open-setup').click(); 'opened'");
            var dialog = await WaitForOpenFormAsync<ShellConnectionDialog>();
            dialog.PartsForTests.Token.Text = NewTypedToken;
            dialog.PartsForTests.Cancel.PerformClick();

            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('toast-text').textContent") == localizer["Shell.Setup.Cancelled"]);
            Assert.Equal(0, source.CheckConnectionCalls + source.ApplyConnectionCalls);
            await WaitUntilAsync(async () => await EvalAsync(core, "String(document.getElementById('settings-open-setup').getAttribute('aria-busy'))") == "null");
        });

    [Fact]
    public Task NotConfigured_OffersConnect_InTheOverviewAndSettings() =>
        RunShellAsync(async (_, core, source) =>
        {
            var localizer = new PrintBridgeLocalizer(CurrentCultureService!);
            source.Set(Status(BridgeServerConnectionStatus.NotConfigured, isRunning: false));

            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('connection').dataset.state") == "notConfigured");
            Assert.Equal(localizer["Shell.Action.Connect"], await EvalAsync(core, "document.querySelector('#hero-actions [data-hero]').textContent"));
            Assert.Equal("connection.openSetup", await EvalAsync(core, "document.querySelector('#hero-actions [data-hero]').getAttribute('data-command')"));
            Assert.Equal(localizer["Shell.Action.Connect"], await EvalAsync(core, "document.getElementById('settings-open-setup').textContent"));
            Assert.Equal(localizer["Shell.Detail.NotConfigured"], await EvalAsync(core, "document.getElementById('connection-detail').textContent"));
        }, savedToken: string.Empty);

    [Fact]
    public Task OperationalSettings_ShowSavedValues_ValidateInput_AndSaveThroughTheHost() =>
        RunShellAsync(async (_, core, _) =>
        {
            var localizer = new PrintBridgeLocalizer(CurrentCultureService!);
            var settings = CurrentSettings!;
            await EvalAsync(core, "document.getElementById('tab-settings').click(); 'opened'");

            Assert.Equal("""["5","1","15",false]""", await EvalAsync(core,
                "JSON.stringify(['ops-idle', 'ops-busy', 'ops-error'].map(id => document.getElementById(id).value).concat([document.getElementById('ops-test-mode').checked]))"));
            Assert.Equal(localizer.GetString("Shell.Settings.Range", 1, 60), await EvalAsync(core, "document.getElementById('ops-busy-range').textContent"));
            Assert.Equal("true", await EvalAsync(core, "document.getElementById('ops-unsaved').hidden.toString()"));

            await SetInputAsync(core, "ops-idle", "0");
            Assert.Equal("""["true","false","true","ops-idle-range ops-invalid"]""", await EvalAsync(core,
                "JSON.stringify([document.getElementById('ops-idle').getAttribute('aria-invalid'), String(document.getElementById('ops-invalid').hidden), " +
                "document.getElementById('ops-save').getAttribute('aria-disabled'), document.getElementById('ops-idle').getAttribute('aria-describedby')])"));

            await SetInputAsync(core, "ops-idle", "30");
            Assert.Equal("""["false","true","false","false"]""", await EvalAsync(core,
                "JSON.stringify([document.getElementById('ops-idle').getAttribute('aria-invalid'), String(document.getElementById('ops-invalid').hidden), " +
                "String(document.getElementById('ops-unsaved').hidden), document.getElementById('ops-save').getAttribute('aria-disabled')])"));

            await EvalAsync(core, "document.getElementById('ops-save').click(); 'saved'");
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('toast-text').textContent") == localizer["Shell.Op.SettingsSaved"]);
            await WaitUntilAsync(async () => await EvalAsync(core, "String(document.getElementById('ops-unsaved').hidden)") == "true");
            Assert.Equal(30, settings.Bridge.IdlePollIntervalSeconds);
            Assert.Equal(30, new PrintBridgeSettingsStore().Load().PrintBridge.IdlePollIntervalSeconds);
            Assert.Equal("30", await EvalAsync(core, "document.getElementById('ops-idle').value"));
        });

    [Fact]
    public Task TurningTestModeOn_AsksInANativeDialog_ThatThePageCannotAnswer() =>
        RunShellAsync(async (form, core, _) =>
        {
            var localizer = new PrintBridgeLocalizer(CurrentCultureService!);
            var settings = CurrentSettings!;
            await EvalAsync(core, "document.getElementById('tab-settings').click(); 'opened'");

            await EvalAsync(core, "(() => { const t = document.getElementById('ops-test-mode'); t.checked = true; t.dispatchEvent(new Event('change')); document.getElementById('ops-save').click(); return 'asked'; })()");
            var declined = await WaitForOpenFormAsync<ShellConfirmDialog>();
            Assert.Same(form, declined.Owner);
            Assert.Equal(localizer["Shell.TestMode.ConfirmTitle"], declined.Text);
            Assert.Same(declined.Cancel, declined.CancelButton);
            declined.Cancel.PerformClick();

            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('toast-text').textContent") == localizer["Shell.Op.TestModeCancelled"]);
            Assert.False(settings.Bridge.DryRun);
            // The unsaved choice stays visible until the user saves or discards it.
            Assert.Equal("false", await EvalAsync(core, "String(document.getElementById('ops-unsaved').hidden)"));

            await EvalAsync(core, "document.getElementById('ops-save').click(); 'asked again'");
            var confirmed = await WaitForOpenFormAsync<ShellConfirmDialog>();
            confirmed.Confirm.PerformClick();

            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('toast-text').textContent") == localizer["Shell.Op.SettingsSaved"]);
            Assert.True(settings.Bridge.DryRun);
            Assert.True(new PrintBridgeSettingsStore().Load().PrintBridge.DryRun);
        });

    [Theory]
    [InlineData("history")]
    [InlineData("settings")]
    public Task TrayTabRequests_OpenTheMatchingTab_InTheSameWindow(string tab) =>
        RunShellAsync(async (form, core, _) =>
        {
            form.Hide();
            form.ShowShell(tab);

            await WaitUntilAsync(async () => await EvalAsync(core, $"document.getElementById('tab-{tab}').getAttribute('aria-selected')") == "true");
            Assert.Equal($"tab-{tab}", await EvalAsync(core, "document.activeElement.id"));
            Assert.Equal("false", await EvalAsync(core, $"String(document.getElementById('panel-{tab}').hidden)"));
            Assert.True(form.Visible);
            Assert.Single(System.Windows.Forms.Application.OpenForms.OfType<PrintBridgeShellForm>());
        });

    /// <summary>
    /// WAS-63 in the real page, in Arabic: the section mirrors, a check offers the newer version, the start asks in a
    /// mirrored native dialog the page cannot answer (declining changes nothing), the download shows an accessible progress
    /// bar without horizontal scrolling at 320 CSS px, and a failure is announced as an alert without its technical code.
    /// </summary>
    [Fact]
    public Task Updates_InArabic_CheckConfirmProgressAndFailure_ComeFromTheHost() =>
        RunShellAsync(async (form, core, _) =>
        {
            var localizer = new PrintBridgeLocalizer(CurrentCultureService!);
            var updates = CurrentUpdates!;
            await EvalAsync(core, "const s = document.getElementById('language'); s.value = 'ar-SA'; s.dispatchEvent(new Event('change')); 'changed'");
            await WaitUntilAsync(async () => await EvalAsync(core, "document.documentElement.dir") == "rtl");
            await EvalAsync(core, "document.getElementById('tab-settings').click(); 'opened'");

            Assert.Equal(FakeUpdates.Installed, await EvalAsync(core, "document.getElementById('update-installed').textContent"));
            Assert.Equal(localizer["Shell.Update.Title"], await EvalAsync(core, "document.getElementById('update-title').textContent"));
            Assert.Equal("""[true,false,true,true]""", await EvalAsync(core, UpdateVisibilityScript));
            Assert.Equal("rtl", await EvalAsync(core, "getComputedStyle(document.getElementById('update-card')).direction"));

            await EvalAsync(core, "document.getElementById('update-check').click(); 'checked'");
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('update-status').textContent")
                                             == localizer.GetString("Shell.Update.Status.Available", FakeUpdates.Newer));
            Assert.Matches(@"\p{IsArabic}", await EvalAsync(core, "document.getElementById('update-status').textContent"));
            Assert.Equal("""[false,false,false,true]""", await EvalAsync(core, UpdateVisibilityScript));
            Assert.Equal(localizer["Shell.Update.Start"], await EvalAsync(core, "document.getElementById('update-start').textContent"));

            // The page cannot confirm: the host asks in a native, mirrored dialog owned by the app window.
            await EvalAsync(core, "document.getElementById('update-start').focus(); document.getElementById('update-start').click(); 'asked'");
            var declined = await WaitForOpenFormAsync<ShellConfirmDialog>();
            Assert.Same(form, declined.Owner);
            Assert.Equal(localizer["Shell.Update.ConfirmTitle"], declined.Text);
            Assert.Equal(RightToLeft.Yes, declined.RightToLeft);
            Assert.True(declined.RightToLeftLayout);
            Assert.Equal([FakeUpdates.Installed, FakeUpdates.Newer], declined.DetailValues.Select(v => v.Text));
            Assert.All(declined.DetailValues, v => Assert.Equal(RightToLeft.No, v.RightToLeft));
            Assert.Same(declined.Cancel, declined.CancelButton);
            declined.Cancel.PerformClick();
            await WaitUntilAsync(async () => await EvalAsync(core, "String(document.getElementById('update-start').getAttribute('aria-busy'))") == "null");
            Assert.Empty(updates.Prepared);

            await EvalAsync(core, "document.getElementById('update-start').click(); 'asked again'");
            var confirmed = await WaitForOpenFormAsync<ShellConfirmDialog>();
            confirmed.Confirm.PerformClick();
            await WaitUntilAsync(() => Task.FromResult(updates.Prepared.Count == 1));
            Assert.Equal([FakeUpdates.Newer], updates.Prepared);

            updates.Current = FakeUpdates.Attempt(updates.NextAttempt, PrintBridgeUpdateStage.Downloading, percent: 40);
            await WaitUntilAsync(async () => await EvalAsync(core, "String(document.getElementById('update-progress-row').hidden)") == "false");
            Assert.Equal("40", await EvalAsync(core, "String(document.getElementById('update-progress').value)"));
            Assert.Equal(localizer["Shell.Update.Progress"], await EvalAsync(core, "document.getElementById('update-progress').getAttribute('aria-label')"));
            Assert.Contains("40", await EvalAsync(core, "document.getElementById('update-progress').getAttribute('aria-valuetext')"));
            Assert.Equal("""[false,true,true,false]""", await EvalAsync(core, UpdateVisibilityScript));
            // The focused start button went away; focus stays in the section, on the cancel button.
            Assert.Equal("update-cancel", await EvalAsync(core, "document.activeElement.id"));

            await core.CallDevToolsProtocolMethodAsync(
                "Emulation.setDeviceMetricsOverride",
                JsonSerializer.Serialize(new { width = 320, height = 420, deviceScaleFactor = 1, mobile = false }));
            await WaitUntilAsync(async () => await EvalAsync(core, "String(document.documentElement.clientWidth)") == "320");
            // The section wraps inside the content area; nothing in it runs past the card or the card past the content.
            var offenders = await EvalAsync(core, """
                (() => {
                  const main = document.querySelector('.pb-main');
                  const area = main.getBoundingClientRect();
                  const card = document.getElementById('update-card').getBoundingClientRect();
                  const out = [];
                  if (main.scrollWidth > main.clientWidth) out.push('content scrolls');
                  if (card.left < area.left - 1 || card.right > area.right + 1) out.push('update-card');
                  for (const el of document.querySelectorAll('#update-card *')) {
                    if (el.closest('[hidden]')) continue;
                    const r = el.getBoundingClientRect();
                    if (r.width === 0 || r.height === 0) continue;
                    if (r.left < card.left - 1 || r.right > card.right + 1) out.push(el.id || el.className || el.tagName);
                  }
                  return JSON.stringify(out);
                })()
                """);
            Assert.True(offenders == "[]", offenders);
            await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");

            updates.Current = FakeUpdates.Attempt(updates.NextAttempt, PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.HashMismatch);
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('alert').textContent") == localizer["Shell.Update.Failure.Verification"]);
            Assert.Equal("danger", await EvalAsync(core, "document.getElementById('update-status').getAttribute('data-tone')"));
            Assert.Equal(localizer["Shell.Update.Retry"], await EvalAsync(core, "document.getElementById('update-start').textContent"));
            var html = await EvalAsync(core, "document.documentElement.outerHTML");
            foreach (var code in new[] { "HashMismatch", "hashMismatch", FakeUpdates.SetupSha256, "update-state", "Setup-x64" })
                Assert.DoesNotContain(code, html, StringComparison.Ordinal);
            Assert.Empty(CurrentLauncher!.Started());
        });

    /// <summary>Whether the status text and the check, start and cancel buttons are hidden, in that order.</summary>
    private const string UpdateVisibilityScript =
        "JSON.stringify(['update-status', 'update-check', 'update-start', 'update-cancel'].map(id => document.getElementById(id).hidden))";

    private const string NewTypedToken = "typed-in-the-dialog-not-a-real-credential";

    private static async Task<T> WaitForOpenFormAsync<T>()
        where T : Form
    {
        T? found = null;
        await WaitUntilAsync(() =>
        {
            found = System.Windows.Forms.Application.OpenForms.OfType<T>().FirstOrDefault(f => f.Visible);
            return Task.FromResult(found is not null);
        });
        return found!;
    }

    private static Task<string?> SetInputAsync(CoreWebView2 core, string id, string value) =>
        EvalAsync(core, $"(() => {{ const i = document.getElementById('{id}'); i.value = '{value}'; i.dispatchEvent(new Event('input')); return 'set'; }})()");

    private static string TabStateScript(string selected, string previous) =>
        $"JSON.stringify([document.activeElement.id, document.getElementById('tab-{selected}').getAttribute('aria-selected'), " +
        $"document.getElementById('panel-{selected}').hidden, document.getElementById('panel-{previous}').hidden, " +
        $"document.getElementById('tab-{previous}').tabIndex])";

    /// <summary>Header labels in every connection state: the title's resource key, the status, and whether it must show in full.</summary>
    private static (string TitleKey, PrintBridgeRuntimeStatus Status, bool FullLabel)[] HeaderStatuses =>
    [
        ("Shell.Connection.Connecting", Status(BridgeServerConnectionStatus.Disconnected), true),
        ("Status.Offline", Status(BridgeServerConnectionStatus.Error, new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServerUnreachable)), true),
        ("ConnectionStatus.NotConfigured", Status(BridgeServerConnectionStatus.NotConfigured, isRunning: false), true),
        ("ConnectionStatus.Stopped", Status(BridgeServerConnectionStatus.Stopped, isRunning: false), true),
        ("ConnectionStatus.Error", Status(BridgeServerConnectionStatus.Error), true),
        // One of the longest titles. The pill caps it at 14rem with an ellipsis (English and Russian); the overview shows it in full.
        ("RuntimeIssue.ServiceUnavailable.Title", Status(BridgeServerConnectionStatus.Error, new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServiceUnavailable)), false),
        ("Status.Online", Status(), true)
    ];

    private static async Task AssertNoHorizontalOverflowOnEveryTabAsync(CoreWebView2 core, string context)
    {
        foreach (var tab in new[] { "overview", "printer", "history", "settings" })
        {
            await EvalAsync(core, $"document.getElementById('tab-{tab}').click(); 'shown'");
            var offenders = await HorizontalOverflowOffendersAsync(core, fullLabel: true);
            Assert.True(offenders == "[]", $"{context}, {tab}: {offenders}");
        }
    }

    /// <summary>
    /// Content may wrap but never overflow the viewport. The tab strip and the history table scroll inside their
    /// own containers, which reflow rules allow for two-dimensional data. In the header (logo, connection status,
    /// language) nothing overlaps, the logo stays inside its box, the language list keeps its natural width and the
    /// status label is not cut unless <paramref name="fullLabel"/> is false.
    /// </summary>
    private static Task<string?> HorizontalOverflowOffendersAsync(CoreWebView2 core, bool fullLabel) =>
        EvalAsync(core, $$"""
            (() => {
              const fullLabel = {{(fullLabel ? "true" : "false")}};
              const width = document.documentElement.clientWidth;
              const name = el => el.id || el.className || el.tagName;
              const box = el => el.getBoundingClientRect();
              const out = [];
              if (document.documentElement.scrollWidth > width) out.push('document');
              for (const el of document.querySelectorAll('#app *')) {
                if (el.closest('[hidden], .pb-visually-hidden, .pb-table-wrap, .pb-tabs')) continue;
                const r = box(el);
                if (r.width === 0 || r.height === 0) continue;
                if (r.right > width + 1 || r.left < -1) out.push(name(el));
              }

              const header = document.querySelector('.pb-header');
              const parts = [...header.children];
              const overlaps = (a, b) => a.left < b.right - 0.5 && b.left < a.right - 0.5 && a.top < b.bottom - 0.5 && b.top < a.bottom - 0.5;
              parts.forEach((a, i) => parts.slice(i + 1).forEach(b => { if (overlaps(box(a), box(b))) out.push('overlap ' + name(a) + ' / ' + name(b)); }));
              const logo = box(document.querySelector('.pb-brand-logo'));
              const brand = box(document.querySelector('.pb-brand'));
              if (logo.left < brand.left - 0.5 || logo.right > brand.right + 0.5) out.push('logo outside its box');
              const label = document.getElementById('status-pill-label');
              if (fullLabel && label.scrollWidth > label.clientWidth) out.push('status label cut: ' + label.textContent);
              const language = document.getElementById('language');
              const natural = language.cloneNode(true);
              natural.removeAttribute('id');
              natural.style.position = 'absolute';
              natural.style.visibility = 'hidden';
              header.append(natural);
              const naturalWidth = box(natural).width;
              natural.remove();
              if (box(language).width < naturalWidth - 0.5) out.push('language list squeezed');
              return JSON.stringify(out.slice(0, 8));
            })()
            """);

    /// <summary>
    /// A real key press through the browser input pipeline, not a synthetic DOM event. It waits until the page can
    /// take input first: Chromium drops input that arrives before a newly loaded page has been painted, and the press
    /// still reports success, so the key would be lost without any error.
    /// </summary>
    private static async Task PressAsync(CoreWebView2 core, string key)
    {
        await WaitUntilThePageTakesInputAsync(core);
        foreach (var type in new[] { "keyDown", "keyUp" })
        {
            await core.CallDevToolsProtocolMethodAsync(
                "Input.dispatchKeyEvent",
                JsonSerializer.Serialize(new { type, key, code = key, windowsVirtualKeyCode = VirtualKey(key) }));
        }
    }

    /// <summary>
    /// The page shows nothing until it has rendered the host's first snapshot (<c>aria-busy="true"</c> hides the app).
    /// Its first contentful paint therefore means that state is on screen and the page has been painted. A sent
    /// snapshot is not enough: the page may not have rendered or painted it yet. The page reports both itself.
    /// </summary>
    private static async Task WaitUntilThePageTakesInputAsync(CoreWebView2 core)
    {
        var state = await EvalAsync(core, """
            new Promise(resolve => {
              const app = document.getElementById('app');
              let painted = false;
              const rendered = new MutationObserver(() => check());
              const paints = new PerformanceObserver(list => {
                painted = painted || list.getEntries().some(entry => entry.name === 'first-contentful-paint');
                check();
              });
              function check() {
                if (!painted || app.getAttribute('aria-busy') !== 'false') return;
                rendered.disconnect();
                paints.disconnect();
                resolve('takes input');
              }
              rendered.observe(app, { attributes: true, attributeFilter: ['aria-busy'] });
              paints.observe({ type: 'paint', buffered: true });
            })
            """).WaitAsync(Timeout);
        Assert.Equal("takes input", state);
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

    /// <summary>The in-memory settings of the window under test; operational settings are saved under the test root.</summary>
    [ThreadStatic]
    private static PrintBridgeSettingsHolder? CurrentSettings;

    /// <summary>The update state of the window under test (WAS-63), and the setup it would start.</summary>
    [ThreadStatic]
    private static FakeUpdates? CurrentUpdates;

    [ThreadStatic]
    private static FakeSetupLauncher? CurrentLauncher;

    [ThreadStatic]
    private static FakeSetupVerifier? CurrentVerifier;

    private Task RunShellAsync(Func<PrintBridgeShellForm, CoreWebView2, FakeStatusSource, Task> body, string savedToken = SentinelToken)
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
                Exception? failure = null;
                try
                {
                    var culture = new PrintBridgeCultureService();
                    culture.Initialize(SupportedCultures.Turkish);
                    CurrentCultureService = culture;
                    var source = new FakeStatusSource();
                    var settings = ShellTestRig.NewSettings(savedToken);
                    CurrentSettings = settings;
                    var store = new PrintBridgeSettingsStore();
                    var localizer = new PrintBridgeLocalizer(culture);
                    var updates = new FakeUpdates(source);
                    CurrentUpdates = updates;
                    var launcher = new FakeSetupLauncher();
                    CurrentLauncher = launcher;
                    var verifier = new FakeSetupVerifier();
                    CurrentVerifier = verifier;
                    using var form = new PrintBridgeShellForm(
                        source,
                        settings,
                        new FakePrinterCatalog(),
                        new FakePrinterSettings(),
                        new EngineOperationalSettings(Wasla.PrintBridge.Tests.Service.TestEngineHost.Create(store, settings)),
                        new ShellConnectionSetup(source, settings, localizer, NullLogger.Instance, CancellationToken.None),
                        localizer,
                        culture,
                        new FakeLanguageSwitcher(culture),
                        "154.0.4258.53",
                        NullLogger.Instance,
                        updates,
                        launcher,
                        verifier)
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
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                // The window is already disposed here, so whatever it writes while closing is saved before the test
                // is reported done; the data root also waits for this thread.
                loop.ExitThread();
                if (failure is null)
                    completion.TrySetResult();
                else
                    completion.TrySetException(failure);
            }, null);
            System.Windows.Forms.Application.Run(loop);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        _dataRoot.Track(thread);
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
