using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Xunit;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The Wasla confirmation dialog that replaced native browser confirms (WAS-119), in a real headless browser against
/// <see cref="TenantVisualWebHost"/> in Mock provider mode with fictional orders: Approve and Mark ready in both preference
/// modes on the Live Screen and Orders, Reject and reprint always asking, Print Bridge token regeneration staying open
/// while it runs, nothing sent on dismissal, keyboard and focus, refreshes, repeated activation, and truthful results when
/// the answer is lost. Same browser and CDN prerequisites as the other headless-browser tests.
/// </summary>
[Trait("Category", "Browser")]
public sealed class OrderActionConfirmBrowserTests : IAsyncLifetime
{
    private static readonly Guid NewOrderId = TenantVisualWebHost.DetailOrderId;
    private static readonly Guid PreparingOrderId = Guid.Parse("83000000-0000-4000-8000-0000000000c2");
    private static readonly Guid PrintedOrderId = Guid.Parse("83000000-0000-4000-8000-0000000000c3");
    private static readonly Guid UnprintedOrderId = Guid.Parse("83000000-0000-4000-8000-0000000000c4");

    // Records every application POST and fails the test run's assumptions if a native dialog is used.
    private const string Recorder = """
        window.__posts = [];
        window.__nativeConfirms = 0;
        window.confirm = () => { window.__nativeConfirms++; return false; };
        const realFetch = window.fetch.bind(window);
        window.__releaseHeld = null;
        window.fetch = (url, options) => {
          const text = String((url && url.url) || url);
          if (window.__failTableReads && text.includes('/orders/table')) return Promise.reject(new TypeError('network'));
          if (window.__blockPrintState && text.includes('/print-state')) return Promise.reject(new TypeError('network'));
          if (window.__holdPreferenceRead && text.includes('/order-action-preference/current')) {
            window.__holdPreferenceRead = false;
            return realFetch(url, options).then(r => new Promise(resolve => { window.__releaseRead = () => resolve(r); }));
          }
          if (options && options.method === 'POST' && !text.includes('/hubs/')) {
            window.__posts.push(text);
            window.__blockPrintState = false;
            if (window.__dropAnswer) { window.__dropAnswer = false; return realFetch(url, options).then(() => { throw new TypeError('answer lost'); }); }
            if (window.__holdThenFail) { window.__holdThenFail = false; return new Promise((_, reject) => { window.__releaseHeld = () => reject(new TypeError('answer lost')); }); }
            if (window.__failNextPost) { window.__failNextPost = false; return Promise.reject(new TypeError('network')); }
            if (window.__answerWith) {
              const status = window.__answerWith; window.__answerWith = 0;
              return Promise.resolve(new Response('{}', { status: status, headers: { 'Content-Type': 'application/json' } }));
            }
            if (window.__holdPosts) return new Promise(resolve => { window.__releaseHeld = () => resolve(realFetch(url, options)); });
          }
          return realFetch(url, options);
        };
        """;

    private TenantVisualWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantVisualWebHost.StartAsync(seed: AddOrders);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Approve_AsksFirst_OneDialogForRepeatedClicks_AndCancellingSendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadLiveAsync(browser, ct);

        Assert.True(await browser.EvaluateAsync<bool>($$"""
            (() => { const b = document.querySelector('#ordersLiveScreenHost [data-order-action="approve"][data-order-id="{{NewOrderId}}"]'); b.click(); b.click(); return true; })()
            """, ct));
        var dialog = await DialogAsync(browser, ct);
        Assert.Equal("Bu sipariş onaylansın mı?", dialog.GetProperty("title").GetString());
        Assert.Equal("Siparişi onayla", dialog.GetProperty("confirm").GetString());
        Assert.Equal("YS-48211", dialog.GetProperty("code").GetString());
        Assert.True(dialog.GetProperty("hasPlatform").GetBoolean(), $"The dialog does not identify the platform: {dialog}");
        Assert.Equal("confirm", dialog.GetProperty("focused").GetString());
        Assert.Equal(1, dialog.GetProperty("modals").GetInt32());
        Assert.Equal(1, dialog.GetProperty("backdrops").GetInt32());

        await browser.PressKeyAsync("Escape", shift: false, ct);
        await WaitClosedAsync(browser, ct);
        Assert.True(await browser.WaitForAsync(
            $"document.activeElement && document.activeElement.getAttribute('data-order-action') === 'approve' && document.activeElement.getAttribute('data-order-id') === '{NewOrderId}'", TimeSpan.FromSeconds(5), ct),
            "Focus did not return to Approve.");
        await AssertPostsAsync(browser, 0, ct);

        await ClickActionAsync(browser, "approve", NewOrderId, ct);
        await DialogAsync(browser, ct);
        await browser.EvaluateAsync<bool>("(() => { document.querySelector('[data-wasla-confirm-cancel]').click(); return true; })()", ct);
        await WaitClosedAsync(browser, ct);
        await AssertPostsAsync(browser, 0, ct);
        Assert.Equal(OrderStatus.New, await StatusAsync(NewOrderId, ct));

        await ClickActionAsync(browser, "approve", NewOrderId, ct);
        await DialogAsync(browser, ct);
        await AcceptAsync(browser, ct);
        await WaitStatusAsync(NewOrderId, OrderStatus.Preparing, ct);
        await AssertPostsAsync(browser, 1, ct);
        Assert.Equal(0, await browser.EvaluateAsync<int>("window.__nativeConfirms", ct));
    }

    [Theory]
    [InlineData("/orders/live-display", "#ordersLiveScreenHost", "approve")]
    [InlineData("/orders/live-display", "#ordersLiveScreenHost", "mark-ready")]
    [InlineData("/orders", "#ordersTableHost", "approve")]
    [InlineData("/orders", "#ordersTableHost", "mark-ready")]
    public async Task PreferenceOff_RoutineActions_SubmitAtOnceWithoutADialog(string path, string host, string action)
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, after, _) = Routine(action);
        await SavePreferenceAsync(confirm: false, ct);
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadAsync(browser, path, host, action, orderId, ct);
        await WaitPreferenceLoadedAsync(browser, confirm: false, ct);

        await browser.EvaluateAsync<bool>($$"""
            (() => { const b = document.querySelector('{{host}} [data-order-action="{{action}}"][data-order-id="{{orderId}}"]'); b.click(); b.click(); return true; })()
            """, ct);
        await WaitStatusAsync(orderId, after, ct);
        await AssertPostsAsync(browser, 1, ct);
        Assert.False(await browser.EvaluateAsync<bool>("!!document.querySelector('#waslaConfirmDialog.show')", ct), "A dialog opened with the preference off.");
    }

    [Theory]
    [InlineData("/orders/live-display", "#ordersLiveScreenHost", "approve")]
    [InlineData("/orders/live-display", "#ordersLiveScreenHost", "mark-ready")]
    [InlineData("/orders", "#ordersTableHost", "approve")]
    [InlineData("/orders", "#ordersTableHost", "mark-ready")]
    public async Task PreferenceOn_RoutineActions_AskOnEveryScreen(string path, string host, string action)
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, after, confirmLabel) = Routine(action);
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadAsync(browser, path, host, action, orderId, ct);

        await browser.EvaluateAsync<bool>($$"""
            (() => { document.querySelector('{{host}} [data-order-action="{{action}}"][data-order-id="{{orderId}}"]').click(); return true; })()
            """, ct);
        var dialog = await DialogAsync(browser, ct);
        Assert.Equal(confirmLabel, dialog.GetProperty("confirm").GetString());
        await AssertPostsAsync(browser, 0, ct);
        await AcceptAsync(browser, ct);
        await WaitStatusAsync(orderId, after, ct);
        await AssertPostsAsync(browser, 1, ct);
    }

    [Fact]
    public async Task Reject_WithoutReasons_AlwaysAsks_EvenWithThePreferenceOff_AndFocusStartsOnCancel()
    {
        var ct = TestContext.Current.CancellationToken;
        await SavePreferenceAsync(confirm: false, ct);
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadLiveAsync(browser, ct);
        await WaitPreferenceLoadedAsync(browser, confirm: false, ct);

        await ClickActionAsync(browser, "reject", NewOrderId, ct);
        var dialog = await DialogAsync(browser, ct);
        Assert.Equal("cancel", dialog.GetProperty("focused").GetString());
        Assert.Equal("Siparişi reddet", dialog.GetProperty("confirm").GetString());
        Assert.True(dialog.GetProperty("hintHidden").GetBoolean(), "A reject must not say it can be turned off.");
        await AssertPostsAsync(browser, 0, ct);

        await AcceptAsync(browser, ct);
        await WaitStatusAsync(NewOrderId, OrderStatus.Cancelled, ct);
        await AssertPostsAsync(browser, 1, ct);
    }

    [Fact]
    public async Task FromTheDetailModal_ItClosesFirst_AndCancelReturnsFocusToTheDetailsButton()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadLiveAsync(browser, ct);
        await browser.EvaluateAsync<bool>($"(() => {{ window.WaslaOrders.liveDetailModal.open('{NewOrderId}', null); return true; }})()", ct);
        Assert.True(await browser.WaitForAsync(
            $"!!document.querySelector('#ordersLiveDetailModal.show [data-order-action=\"approve\"][data-order-id=\"{NewOrderId}\"]')", TimeSpan.FromSeconds(10), ct),
            (_host.ServerErrors ?? "") + " The detail modal did not show Approve.");

        await browser.EvaluateAsync<bool>($"(() => {{ document.querySelector('#ordersLiveDetailModal [data-order-action=\"approve\"]').click(); return true; }})()", ct);
        var dialog = await DialogAsync(browser, ct);
        Assert.Equal(1, dialog.GetProperty("modals").GetInt32());
        Assert.False(await browser.EvaluateAsync<bool>("!!document.querySelector('#ordersLiveDetailModal.show')", ct), "The detail modal stayed open under the dialog.");

        await browser.PressKeyAsync("Escape", shift: false, ct);
        await WaitClosedAsync(browser, ct);
        Assert.True(await browser.WaitForAsync(
            $"document.activeElement && document.activeElement.getAttribute('data-order-detail') === '{NewOrderId}'", TimeSpan.FromSeconds(5), ct),
            "Focus did not return to the order's details button.");
        await AssertPostsAsync(browser, 0, ct);
    }

    [Fact]
    public async Task ARefresh_ThatChangesTheOrder_TakesAwayTheActionWithoutRetargeting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadLiveAsync(browser, ct);
        await ClickActionAsync(browser, "approve", NewOrderId, ct);
        await DialogAsync(browser, ct);

        await browser.EvaluateAsync<bool>($$"""
            (() => { document.dispatchEvent(new CustomEvent('wasla:live-rendered', { detail: { orders: [{ id: '{{NewOrderId}}', status: 'Preparing' }] } })); return true; })()
            """, ct);

        Assert.True(await browser.WaitForAsync(
            "document.querySelector('[data-wasla-confirm-accept]').hidden && !document.querySelector('[data-wasla-confirm-notice]').hidden", TimeSpan.FromSeconds(5), ct),
            "The dialog kept its action for an order that changed.");
        Assert.Contains("Hazırlanıyor", await browser.EvaluateAsync<string>("document.querySelector('[data-wasla-confirm-notice]').textContent", ct), StringComparison.Ordinal);
        Assert.Equal("YS-48211", await browser.EvaluateAsync<string>("document.querySelector('.wasla-confirm-identity__primary').textContent", ct));

        await browser.EvaluateAsync<bool>("(() => { document.querySelector('[data-wasla-confirm-cancel]').click(); return true; })()", ct);
        await WaitClosedAsync(browser, ct);
        await AssertPostsAsync(browser, 0, ct);
    }

    [Fact]
    public async Task ARefresh_ThatLeavesTheOrderAlone_KeepsTheDialog_AndSubmitsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadLiveAsync(browser, ct);
        await ClickActionAsync(browser, "approve", NewOrderId, ct);
        await DialogAsync(browser, ct);

        await browser.EvaluateAsync<bool>($$"""
            (() => {
              for (let i = 0; i < 3; i++) document.dispatchEvent(new CustomEvent('wasla:live-rendered', { detail: { orders: [{ id: '{{NewOrderId}}', status: 'New' }] } }));
              return true;
            })()
            """, ct);
        Assert.True(await browser.EvaluateAsync<bool>("!document.querySelector('[data-wasla-confirm-accept]').hidden && document.querySelector('[data-wasla-confirm-notice]').hidden", ct),
            "A refresh that changed nothing took the action away.");

        await AcceptAsync(browser, ct);
        await WaitStatusAsync(NewOrderId, OrderStatus.Preparing, ct);
        await AssertPostsAsync(browser, 1, ct);
    }

    [Fact]
    public async Task ARefusedAction_SaysSo_ChangesNothing_AndIsNotRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadLiveAsync(browser, ct);
        await browser.EvaluateAsync<bool>("(() => { window.__answerWith = 409; return true; })()", ct);

        await ClickActionAsync(browser, "approve", NewOrderId, ct);
        await DialogAsync(browser, ct);
        await AcceptAsync(browser, ct);

        Assert.True(await browser.WaitForAsync(
            "[...document.querySelectorAll('.wasla-toast--error .wasla-toast__message')].some(t => t.textContent.includes('İşlem başarısız'))", TimeSpan.FromSeconds(10), ct),
            "A refused action was not reported as a failure.");
        await AssertPostsAsync(browser, 1, ct);
        Assert.Equal(OrderStatus.New, await StatusAsync(NewOrderId, ct));
        Assert.False(await browser.EvaluateAsync<bool>(
            "[...document.querySelectorAll('.wasla-toast--success')].length > 0", ct), "A refused action also reported success.");
    }

    [Fact]
    public async Task ALostAnswer_IsNotAFailureAndIsNotRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadLiveAsync(browser, ct);
        await browser.EvaluateAsync<bool>("(() => { window.__failNextPost = true; return true; })()", ct);

        await ClickActionAsync(browser, "approve", NewOrderId, ct);
        await DialogAsync(browser, ct);
        await AcceptAsync(browser, ct);

        Assert.True(await browser.WaitForAsync(
            "[...document.querySelectorAll('.wasla-toast__message')].some(t => t.textContent.includes('platform panelini'))", TimeSpan.FromSeconds(15), ct),
            "The page did not report that the result could not be confirmed.");
        await AssertPostsAsync(browser, 1, ct);
        Assert.Equal(OrderStatus.New, await StatusAsync(NewOrderId, ct));
    }

    [Theory]
    [InlineData("/orders/live-display", "#ordersLiveScreenHost")]
    [InlineData("/orders", "#ordersTableHost")]
    public async Task ALostAnswer_WhenTheActionWasApplied_ReportsItDone_AndIsNotRetried(string path, string host)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadAsync(browser, path, host, "approve", NewOrderId, ct);
        await browser.EvaluateAsync<bool>($$"""
            (() => { window.__dropAnswer = true; document.querySelector('{{host}} [data-order-action="approve"][data-order-id="{{NewOrderId}}"]').click(); return true; })()
            """, ct);
        await DialogAsync(browser, ct);
        await AcceptAsync(browser, ct);

        await WaitStatusAsync(NewOrderId, OrderStatus.Preparing, ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('.wasla-toast--success')", TimeSpan.FromSeconds(15), ct),
            "An applied action whose answer was lost was not reported as done.");
        await AssertPostsAsync(browser, 1, ct);
    }

    [Fact]
    public async Task ALostAnswer_WhenTheOrderMovedElsewhere_SaysWhereItIs_NotThatItWorked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadLiveAsync(browser, ct);
        await browser.EvaluateAsync<bool>("(() => { window.__holdThenFail = true; return true; })()", ct);

        await ClickActionAsync(browser, "reject", NewOrderId, ct);
        await DialogAsync(browser, ct);
        await AcceptAsync(browser, ct);
        Assert.True(await browser.WaitForAsync("!!window.__releaseHeld", TimeSpan.FromSeconds(5), ct), "The reject was not sent.");

        await using (var db = await TenantDbAsync(ct))
            await db.Orders.Where(o => o.Id == NewOrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.InternalStatus, OrderStatus.Preparing), ct);
        await browser.EvaluateAsync<bool>("(() => { window.__releaseHeld(); return true; })()", ct);

        Assert.True(await browser.WaitForAsync(
            "[...document.querySelectorAll('.wasla-toast--warning .wasla-toast__message')].some(t => t.textContent.includes('Hazırlanıyor'))", TimeSpan.FromSeconds(15), ct),
            "The page did not say what happened to the order.");
        Assert.False(await browser.EvaluateAsync<bool>("!!document.querySelector('.wasla-toast--success')", ct), "A reject that did not happen was reported as done.");
        await AssertPostsAsync(browser, 1, ct);
    }

    [Fact]
    public async Task ALostAnswer_OnTheOrdersPage_WhenTheListCannotBeRead_DoesNotClaimTheOrderIsWaiting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadAsync(browser, "/orders", "#ordersTableHost", "approve", NewOrderId, ct);
        await browser.EvaluateAsync<bool>($$"""
            (() => { window.__failNextPost = true; window.__failTableReads = true; document.querySelector('#ordersTableHost [data-order-action="approve"][data-order-id="{{NewOrderId}}"]').click(); return true; })()
            """, ct);
        await DialogAsync(browser, ct);
        await AcceptAsync(browser, ct);

        Assert.True(await browser.WaitForAsync(
            "[...document.querySelectorAll('.wasla-toast__message')].some(t => t.textContent.includes('Sipariş durumunu kontrol edin'))", TimeSpan.FromSeconds(15), ct),
            "The page claimed to know the order's state after reading nothing.");
        await AssertPostsAsync(browser, 1, ct);
    }

    [Theory]
    [InlineData(PrintJobStatus.Printed)]
    [InlineData(PrintJobStatus.Failed)]
    public async Task AStaleFirstPrintButton_CreatesNoExtraCopy_AndReprintStillAsks(PrintJobStatus finished)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await browser.NavigateAsync(new Uri(_host.BaseAddress, $"/orders/details/{UnprintedOrderId}"), ct);
        Assert.True(await browser.WaitForAsync("document.querySelector('[data-order-print]')?.getAttribute('data-print-mode') === 'print'", TimeSpan.FromSeconds(15), ct),
            (_host.ServerErrors ?? "") + " The unprinted order did not offer a first print.");

        // Another operator's receipt, or the automatic one, finishes while this page still offers a first print.
        await browser.EvaluateAsync<bool>("(() => { window.__blockPrintState = true; return true; })()", ct);
        await AddFinishedReceiptAsync(UnprintedOrderId, finished, ct);

        await browser.EvaluateAsync<bool>("(() => { document.querySelector('[data-order-print]').click(); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("window.__posts.length === 1", TimeSpan.FromSeconds(10), ct), "The print request was not sent.");
        Assert.True(await browser.WaitForAsync("document.querySelector('[data-order-print]').getAttribute('data-print-mode') !== 'print'", TimeSpan.FromSeconds(10), ct), "The button did not leave its stale state.");

        Assert.Equal(1, await ReceiptJobCountAsync(UnprintedOrderId, ct));
        Assert.False(await browser.EvaluateAsync<bool>("!!document.querySelector('#waslaConfirmDialog.show')", ct), "A first print asked for confirmation.");
        Assert.True(await browser.WaitForAsync(
            "[...document.querySelectorAll('.wasla-toast--warning .wasla-toast__message')].some(t => t.textContent.includes('yeni fiş oluşturulmadı'))", TimeSpan.FromSeconds(5), ct),
            "The page did not say that nothing was created.");
        Assert.False(await browser.EvaluateAsync<bool>("!!document.querySelector('.wasla-toast--success')", ct), "A refused first print was reported as queued.");

        Assert.True(await browser.WaitForAsync("(() => { const b = document.querySelector('[data-order-print]'); return b.getAttribute('data-print-mode') === 'reprint' && !b.disabled; })()", TimeSpan.FromSeconds(10), ct),
            "The button did not end on Reprint.");
        await browser.EvaluateAsync<bool>("(() => { document.querySelector('[data-order-print]').click(); return true; })()", ct);
        await DialogAsync(browser, ct);
        await AssertPostsAsync(browser, 1, ct);
        await AcceptAsync(browser, ct);
        Assert.True(await browser.WaitForAsync("window.__posts.length === 2", TimeSpan.FromSeconds(10), ct), "The confirmed reprint was not sent.");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await ReceiptJobCountAsync(UnprintedOrderId, ct) < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(100, ct);
        Assert.Equal(2, await ReceiptJobCountAsync(UnprintedOrderId, ct));
    }

    [Fact]
    public async Task Reprint_ALostAnswer_SaysUnverified_NotFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await browser.NavigateAsync(new Uri(_host.BaseAddress, $"/orders/details/{PrintedOrderId}"), ct);
        Assert.True(await browser.WaitForAsync("document.querySelector('[data-order-print]')?.getAttribute('data-print-mode') === 'reprint'", TimeSpan.FromSeconds(15), ct),
            (_host.ServerErrors ?? "") + " The printed order did not offer Reprint.");

        await browser.EvaluateAsync<bool>("(() => { window.__failNextPost = true; document.querySelector('[data-order-print]').click(); return true; })()", ct);
        await DialogAsync(browser, ct);
        await AcceptAsync(browser, ct);

        Assert.True(await browser.WaitForAsync(
            "[...document.querySelectorAll('.wasla-toast--warning .wasla-toast__message')].some(t => t.textContent.includes('doğrulanamadı'))", TimeSpan.FromSeconds(10), ct),
            "A lost reprint answer was not reported as unverified.");
        Assert.False(await browser.EvaluateAsync<bool>("!!document.querySelector('.wasla-toast--error')", ct), "A lost reprint answer was reported as a failure.");
        await AssertPostsAsync(browser, 1, ct);
    }

    [Fact]
    public async Task Reprint_AlwaysAsks_AndAFirstPrintDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await SavePreferenceAsync(confirm: false, ct);
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await browser.NavigateAsync(new Uri(_host.BaseAddress, $"/orders/details/{PrintedOrderId}"), ct);
        Assert.True(await browser.WaitForAsync("document.querySelector('[data-order-print]')?.getAttribute('data-print-mode') === 'reprint'", TimeSpan.FromSeconds(15), ct),
            (_host.ServerErrors ?? "") + " The printed order did not offer Reprint.");

        await browser.EvaluateAsync<bool>("(() => { const b = document.querySelector('[data-order-print]'); b.click(); b.click(); return true; })()", ct);
        var dialog = await DialogAsync(browser, ct);
        Assert.Equal("Fiş tekrar yazdırılsın mı?", dialog.GetProperty("title").GetString());
        Assert.Equal("cancel", dialog.GetProperty("focused").GetString());

        await browser.PressKeyAsync("Escape", shift: false, ct);
        await WaitClosedAsync(browser, ct);
        await AssertPostsAsync(browser, 0, ct);

        await browser.EvaluateAsync<bool>("(() => { document.querySelector('[data-order-print]').click(); return true; })()", ct);
        await DialogAsync(browser, ct);
        await AcceptAsync(browser, ct);
        Assert.True(await browser.WaitForAsync("window.__posts.length === 1", TimeSpan.FromSeconds(10), ct), "No reprint was posted.");
        Assert.EndsWith($"/orders/{PrintedOrderId}/print", await browser.EvaluateAsync<string>("window.__posts[0]", ct), StringComparison.Ordinal);

        await browser.NavigateAsync(new Uri(_host.BaseAddress, $"/orders/details/{UnprintedOrderId}"), ct);
        Assert.True(await browser.WaitForAsync("document.querySelector('[data-order-print]')?.getAttribute('data-print-mode') === 'print'", TimeSpan.FromSeconds(15), ct),
            "The unprinted order did not offer a first print.");
        await browser.EvaluateAsync<bool>("(() => { document.querySelector('[data-order-print]').click(); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("window.__posts.length === 1", TimeSpan.FromSeconds(10), ct), "The first print was not sent directly.");
        Assert.False(await browser.EvaluateAsync<bool>("!!document.querySelector('#waslaConfirmDialog.show')", ct), "A first print asked for confirmation.");
    }

    [Fact]
    public async Task TokenRegeneration_AsksEvenWithThePreferenceOff_StaysOpenWhileItRuns_AndCannotBeDismissed()
    {
        var ct = TestContext.Current.CancellationToken;
        await SavePreferenceAsync(confirm: false, ct);
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await OpenFirstDeviceAsync(browser, ct);

        await browser.EvaluateAsync<bool>("(() => { document.querySelector('#printBridgeDetailRegenerateTokenBtn').click(); return true; })()", ct);
        var dialog = await DialogAsync(browser, ct);
        Assert.Equal("Token yenilensin mi?", dialog.GetProperty("title").GetString());
        Assert.Equal("cancel", dialog.GetProperty("focused").GetString());
        await browser.PressKeyAsync("Escape", shift: false, ct);
        await WaitClosedAsync(browser, ct);
        await AssertPostsAsync(browser, 0, ct);

        await browser.EvaluateAsync<bool>("(() => { window.__holdPosts = true; document.querySelector('#printBridgeDetailRegenerateTokenBtn').click(); return true; })()", ct);
        await DialogAsync(browser, ct);
        await AcceptAsync(browser, ct, closes: false);
        Assert.True(await browser.WaitForAsync("window.__posts.length === 1 && !!window.__releaseHeld", TimeSpan.FromSeconds(5), ct), "The request was not sent.");

        await browser.PressKeyAsync("Escape", shift: false, ct);
        Assert.True(await browser.EvaluateAsync<bool>("!!document.querySelector('#waslaConfirmDialog.show') && document.querySelector('[data-wasla-confirm-accept]').disabled", ct),
            "The dialog could be dismissed while the request was out.");
        await AssertPostsAsync(browser, 1, ct);

        await browser.EvaluateAsync<bool>("(() => { window.__releaseHeld(); return true; })()", ct);
        await WaitClosedAsync(browser, ct);
        Assert.True(await browser.WaitForAsync("!document.getElementById('printBridgeDetailTokenBox').classList.contains('d-none')", TimeSpan.FromSeconds(10), ct),
            (_host.ServerErrors ?? "") + " The new token was not shown.");
    }

    [Theory]
    [InlineData("#printBridgeDetailRegenerateTokenBtn", 502)]
    [InlineData("#printBridgeDetailRemoveBtn", 502)]
    [InlineData("#printBridgeDetailRemoveBtn", 0)]
    public async Task PrintBridge_ALostAnswer_IsReportedAsUnknown_WithoutBrowserErrorText(string button, int status)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await OpenFirstDeviceAsync(browser, ct);

        var arrange = status == 0 ? "window.__failNextPost = true" : $"window.__answerWith = {status}";
        await browser.EvaluateAsync<bool>($$"""(() => { {{arrange}}; document.querySelector('{{button}}').click(); return true; })()""", ct);
        await DialogAsync(browser, ct);
        await AcceptAsync(browser, ct, closes: false);

        Assert.True(await browser.WaitForAsync("!document.querySelector('[data-wasla-confirm-notice]').hidden && document.querySelector('[data-wasla-confirm-accept]').hidden", TimeSpan.FromSeconds(5), ct),
            "A lost answer did not stay in the dialog.");
        Assert.Equal("Sonuç doğrulanamadı", await browser.EvaluateAsync<string>("document.querySelector('[data-wasla-confirm-title]').textContent", ct));
        Assert.DoesNotContain("fetch", await browser.EvaluateAsync<string>("document.querySelector('[data-wasla-confirm-notice]').textContent", ct), StringComparison.OrdinalIgnoreCase);
        await AssertPostsAsync(browser, 1, ct);
    }

    [Fact]
    public async Task APreferenceReadThatStartedDuringASave_CannotUndoIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadLiveAsync(browser, ct);
        await WaitPreferenceLoadedAsync(browser, confirm: true, ct);

        await browser.EvaluateAsync<bool>("(() => { window.__holdPosts = true; document.querySelector('#ordersActionPreferenceSwitch').click(); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("!!window.__releaseHeld", TimeSpan.FromSeconds(5), ct), "The save was not sent.");
        await browser.EvaluateAsync<bool>("(() => { window.__holdPreferenceRead = true; window.WaslaOrders.actionPreference.refresh(); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("!!window.__releaseRead", TimeSpan.FromSeconds(5), ct), "The read did not answer.");

        await browser.EvaluateAsync<bool>("(() => { window.__holdPosts = false; window.__releaseHeld(); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("!document.querySelector('#ordersActionPreferenceSwitch').checked", TimeSpan.FromSeconds(5), ct), "The saved value was not shown.");
        await browser.EvaluateAsync<bool>("(() => { window.__releaseRead(); return true; })()", ct);
        await Task.Delay(200, ct);

        Assert.False(await browser.EvaluateAsync<bool>("window.WaslaOrders.actionPreference.confirmsRoutineActions() || document.querySelector('#ordersActionPreferenceSwitch').checked", ct),
            "A read from before the save undid it.");
        Assert.False(await StoredPreferenceAsync(ct));
    }

    [Fact]
    public async Task DeviceRemoval_AsksEvenWithThePreferenceOff_AndARefusalStaysInTheDialog()
    {
        var ct = TestContext.Current.CancellationToken;
        await SavePreferenceAsync(confirm: false, ct);
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await OpenFirstDeviceAsync(browser, ct);

        await browser.EvaluateAsync<bool>("(() => { window.__answerWith = 400; document.querySelector('#printBridgeDetailRemoveBtn').click(); return true; })()", ct);
        var dialog = await DialogAsync(browser, ct);
        Assert.Equal("cancel", dialog.GetProperty("focused").GetString());
        await AcceptAsync(browser, ct, closes: false);

        Assert.True(await browser.WaitForAsync("!document.querySelector('[data-wasla-confirm-notice]').hidden && document.querySelector('[data-wasla-confirm-accept]').hidden", TimeSpan.FromSeconds(5), ct),
            "A refused removal did not stay in the dialog.");
        Assert.Equal("Cihaz kaldırılamadı", await browser.EvaluateAsync<string>("document.querySelector('[data-wasla-confirm-title]').textContent", ct));
        Assert.True(await browser.EvaluateAsync<bool>("document.activeElement === document.querySelector('[data-wasla-confirm-cancel]')", ct), "Focus did not move to Close.");
    }

    [Fact]
    public async Task Preference_SavesFromTheSettingsMenu_ShowsOnlyStoredValues_AndAFailureChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadLiveAsync(browser, ct);
        await browser.EvaluateAsync<bool>("(() => { document.querySelector('#ordersLiveDisplaySettingsToggle').click(); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('.dropdown-menu.show #ordersActionPreferenceSwitch')", TimeSpan.FromSeconds(5), ct), "The preference is not in the menu.");
        Assert.True(await browser.EvaluateAsync<bool>("document.querySelector('#ordersActionPreferenceSwitch').checked", ct), "The preference does not start enabled.");

        await browser.EvaluateAsync<bool>("(() => { window.__answerWith = 500; document.querySelector('#ordersActionPreferenceSwitch').click(); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("!document.querySelector('[data-order-action-preference-error]').hidden", TimeSpan.FromSeconds(5), ct), "A failed save was not reported.");
        Assert.True(await browser.EvaluateAsync<bool>("document.querySelector('#ordersActionPreferenceSwitch').checked && !!document.querySelector('.dropdown-menu.show')", ct), "A failed save changed the switch or closed the menu.");
        Assert.True(await StoredPreferenceAsync(ct));

        await browser.EvaluateAsync<bool>("(() => { window.__holdPosts = true; document.querySelector('[data-order-action-preference-retry]').click(); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("!!window.__releaseHeld", TimeSpan.FromSeconds(5), ct), "The retry was not sent.");
        Assert.True(await browser.EvaluateAsync<bool>("document.querySelector('#ordersActionPreferenceSwitch').checked", ct), "The switch moved before the server answered.");
        await browser.EvaluateAsync<bool>("(() => { window.__holdPosts = false; window.__releaseHeld(); return true; })()", ct);
        Assert.True(await browser.WaitForAsync("!document.querySelector('#ordersActionPreferenceSwitch').checked", TimeSpan.FromSeconds(5), ct), "The saved value was not shown.");
        Assert.False(await StoredPreferenceAsync(ct));

        await LoadLiveAsync(browser, ct);
        await WaitPreferenceLoadedAsync(browser, confirm: false, ct);
    }

    [Fact]
    public async Task Kitchen_HasThePreference_InTheSettingsMenu()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct, signInAs: "kitchen");
        await LoadLiveAsync(browser, ct);
        Assert.True(await browser.EvaluateAsync<bool>("!!document.querySelector('[data-order-action-preference]')", ct), "Kitchen does not have the preference.");
    }

    [Fact]
    public async Task Arabic_Phone_TheDialogIsMirroredAndFits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("ar-SA", 390, 844, ct);
        await LoadLiveAsync(browser, ct);

        await ClickActionAsync(browser, "approve", NewOrderId, ct);
        await DialogAsync(browser, ct);

        var layout = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const d = document.querySelector('#waslaConfirmDialog .modal-content').getBoundingClientRect();
              const confirm = document.querySelector('[data-wasla-confirm-accept]').getBoundingClientRect();
              const cancel = document.querySelector('[data-wasla-confirm-cancel]').getBoundingClientRect();
              const title = document.querySelector('[data-wasla-confirm-title]').getBoundingClientRect();
              const close = document.querySelector('[data-wasla-confirm-close]').getBoundingClientRect();
              return {
                closeAtFarEdge: close.left - d.left <= 24 && title.left - close.right >= 8,
                dir: getComputedStyle(document.querySelector('#waslaConfirmDialog .modal-body')).direction,
                left: d.left, right: d.right, width: window.innerWidth,
                confirmBeforeCancel: confirm.right <= cancel.left + 1,
                sideways: document.documentElement.scrollWidth - window.innerWidth
              };
            })()
            """, ct);
        Assert.Equal("rtl", layout.GetProperty("dir").GetString());
        Assert.True(layout.GetProperty("left").GetDouble() >= 0 && layout.GetProperty("right").GetDouble() <= layout.GetProperty("width").GetDouble(), $"The dialog does not fit: {layout}");
        Assert.True(layout.GetProperty("confirmBeforeCancel").GetBoolean(), $"The buttons are not mirrored: {layout}");
        Assert.True(layout.GetProperty("closeAtFarEdge").GetBoolean(), $"The close button is not at the far edge, apart from the title: {layout}");
        Assert.True(layout.GetProperty("sideways").GetDouble() <= 0, $"Horizontal overflow: {layout}");
    }

    [Fact]
    public async Task UnderReducedMotion_TheDialogDoesNotAnimate_AndStillSendsNothingOnEscape()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await browser.SetReducedMotionAsync("reduce", ct);
        await LoadLiveAsync(browser, ct);

        await ClickActionAsync(browser, "approve", NewOrderId, ct);
        await DialogAsync(browser, ct);
        Assert.Equal("0s", await browser.EvaluateAsync<string>("getComputedStyle(document.querySelector('#waslaConfirmDialog')).transitionDuration", ct));

        await browser.PressKeyAsync("Escape", shift: false, ct);
        await WaitClosedAsync(browser, ct);
        await AssertPostsAsync(browser, 0, ct);

        await browser.EvaluateAsync<bool>("""
            (() => { const s = document.createElement('span'); s.className = 'spinner-border wasla-action-spinner'; s.id = 'probe'; document.body.appendChild(s); return true; })()
            """, ct);
        Assert.Equal("none", await browser.EvaluateAsync<string>("getComputedStyle(document.getElementById('probe')).animationName", ct));
    }

    private static void AddOrders(TenantDbContext db, DateTime now)
    {
        db.Orders.AddRange(
            Order(PreparingOrderId, FoodPlatform.Yemeksepeti, "YS-48212", OrderStatus.Preparing, now),
            Order(PrintedOrderId, FoodPlatform.Yemeksepeti, "YS-48213", OrderStatus.Preparing, now),
            Order(UnprintedOrderId, FoodPlatform.Yemeksepeti, "YS-48214", OrderStatus.Preparing, now));
        db.PrintJobs.Add(new PrintJob
        {
            OrderId = PrintedOrderId,
            Type = PrintJobType.Receipt,
            Status = PrintJobStatus.Printed,
            CopyCount = 1,
            PayloadJson = "{}",
            AttemptCount = 1,
            PrintedAt = now.AddMinutes(-1)
        });
    }

    private static Order Order(Guid id, FoodPlatform platform, string code, OrderStatus status, DateTime now) => new()
    {
        Id = id,
        Platform = platform,
        ExternalOrderId = code,
        ExternalOrderCode = code,
        IdempotencyKey = $"{platform}:{code}",
        InternalStatus = status,
        PlatformStatus = "Created",
        CustomerName = "Derya Polat",
        CustomerPhone = "+90 500 000 00 09",
        CustomerAddress = "Örnek Mah. Deneme Sok. No: 5, Kadıköy / İstanbul",
        TotalAmount = 210m,
        PaymentMethod = PaymentMethod.OnlinePayment,
        PaymentStatus = PaymentStatus.Paid,
        CreatedAtPlatform = now.AddMinutes(-1),
        ReceivedAt = now.AddMinutes(-1),
        AcceptedAt = status == OrderStatus.New ? null : now.AddSeconds(-30),
        RawPayloadJson = "{}",
        Items = [new OrderItem { ProductName = "Mercimek Çorbası", Quantity = 2, UnitPrice = 105m, TotalPrice = 210m }]
    };

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, int height, CancellationToken ct, string? signInAs = null)
    {
        var browser = await _host.BrowserAsync(culture, width, height, mobile: width < 992, ct, signInAs);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the confirmation checks.");
        return await browser.SetUpAsync(async started =>
        {
            await started.NavigateAsync(new Uri(_host.BaseAddress, "/css/site.css"), ct);
            await started.EvaluateAsync<bool>("(() => { localStorage.clear(); localStorage.setItem('Wasla.liveScreen.viewMode', 'board'); return true; })()", ct);
            await started.AddScriptBeforePageScriptsAsync(Recorder, ct);
        });
    }

    private Task LoadLiveAsync(HeadlessChromium browser, CancellationToken ct) =>
        LoadAsync(browser, "/orders/live-display", "#ordersLiveScreenHost", "approve", NewOrderId, ct);

    private async Task LoadAsync(HeadlessChromium browser, string path, string host, string action, Guid orderId, CancellationToken ct)
    {
        await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        Assert.True(await browser.WaitForAsync(
            $"!!document.querySelector('{host} [data-order-action=\"{action}\"][data-order-id=\"{orderId}\"]')", TimeSpan.FromSeconds(15), ct),
            (_host.ServerErrors ?? "") + $" {path} did not render {action} for {orderId}.");
    }

    private async Task OpenFirstDeviceAsync(HeadlessChromium browser, CancellationToken ct)
    {
        await browser.NavigateAsync(new Uri(_host.BaseAddress, "/print-bridge/devices"), ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('a[href*=\"/print-bridge/devices/\"]')", TimeSpan.FromSeconds(15), ct),
            (_host.ServerErrors ?? "") + " No device link on the Devices page.");
        var href = await browser.EvaluateAsync<string>("document.querySelector('a[href*=\"/print-bridge/devices/\"]').getAttribute('href')", ct);
        await browser.NavigateAsync(new Uri(_host.BaseAddress, href), ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('#printBridgeDetailRegenerateTokenBtn')", TimeSpan.FromSeconds(15), ct),
            (_host.ServerErrors ?? "") + " The device details page did not load.");
    }

    private static (Guid OrderId, OrderStatus After, string ConfirmLabel) Routine(string action) => action == "approve"
        ? (NewOrderId, OrderStatus.Preparing, "Siparişi onayla")
        : (PreparingOrderId, OrderStatus.ReadyForPickup, "Hazır olarak işaretle");

    private static async Task ClickActionAsync(HeadlessChromium browser, string action, Guid orderId, CancellationToken ct) =>
        Assert.True(await browser.EvaluateAsync<bool>(
            $"(() => {{ const b = document.querySelector('#ordersLiveScreenHost [data-order-action=\"{action}\"][data-order-id=\"{orderId}\"]'); if (!b) return false; b.click(); return true; }})()", ct),
            $"No {action} for {orderId}.");

    private static async Task<JsonElement> DialogAsync(HeadlessChromium browser, CancellationToken ct)
    {
        Assert.True(await browser.WaitForAsync(
            "!!document.querySelector('#waslaConfirmDialog.show') && document.querySelector('#waslaConfirmDialog').contains(document.activeElement) && document.activeElement !== document.querySelector('#waslaConfirmDialog')",
            TimeSpan.FromSeconds(5), ct), "The confirmation dialog did not open with focus on a control.");
        return await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const accept = document.querySelector('[data-wasla-confirm-accept]');
              const cancel = document.querySelector('[data-wasla-confirm-cancel]');
              const code = document.querySelector('.wasla-confirm-identity__primary');
              return {
                title: document.querySelector('[data-wasla-confirm-title]').textContent,
                confirm: accept.textContent,
                code: code ? code.textContent : null,
                hasPlatform: !!document.querySelector('[data-wasla-confirm-identity] .platform-badge'),
                hintHidden: document.querySelector('[data-wasla-confirm-hint]').hidden,
                focused: document.activeElement === accept ? 'confirm' : document.activeElement === cancel ? 'cancel' : 'other',
                modals: document.querySelectorAll('.modal.show').length,
                backdrops: document.querySelectorAll('.modal-backdrop').length
              };
            })()
            """, ct);
    }

    /// <summary>Clicks the confirm button until the dialog takes it (it ignores activation for a moment after opening).</summary>
    private static async Task AcceptAsync(HeadlessChromium browser, CancellationToken ct, bool closes = true)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var taken = await browser.EvaluateAsync<bool>($$"""
                (() => {
                  const a = document.querySelector('[data-wasla-confirm-accept]');
                  if (!document.querySelector('#waslaConfirmDialog.show') || a.hidden || a.disabled) return true;
                  a.click();
                  return !document.querySelector('#waslaConfirmDialog.show') || a.disabled;
                })()
                """, ct);
            if (taken) break;
            await Task.Delay(50, ct);
        }

        if (closes)
            await WaitClosedAsync(browser, ct);
    }

    private static async Task WaitClosedAsync(HeadlessChromium browser, CancellationToken ct) =>
        Assert.True(await browser.WaitForAsync("!document.querySelector('.modal.show, .modal-backdrop')", TimeSpan.FromSeconds(5), ct), "The dialog did not close.");

    private static async Task AssertPostsAsync(HeadlessChromium browser, int expected, CancellationToken ct)
    {
        await Task.Delay(150, ct);
        var actual = await browser.EvaluateAsync<int>("window.__posts.length", ct);
        Assert.True(expected == actual, $"Expected {expected} action request(s), saw {actual}: {await browser.EvaluateAsync<string>("window.__posts.join(', ')", ct)}");
    }

    private static async Task WaitPreferenceLoadedAsync(HeadlessChromium browser, bool confirm, CancellationToken ct) =>
        Assert.True(await browser.WaitForAsync(
            $"window.WaslaOrders.actionPreference.confirmsRoutineActions() === {(confirm ? "true" : "false")}", TimeSpan.FromSeconds(10), ct),
            $"The page did not read the stored preference ({confirm}).");

    private async Task SavePreferenceAsync(bool confirm, CancellationToken ct)
    {
        await using var db = await TenantDbAsync(ct);
        db.UserOrderActionPreferences.Add(new UserOrderActionPreference { UserId = TenantVisualWebHost.OwnerId, ConfirmRoutineActions = confirm });
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> StoredPreferenceAsync(CancellationToken ct)
    {
        await using var db = await TenantDbAsync(ct);
        return await db.UserOrderActionPreferences.AsNoTracking().Where(p => p.UserId == TenantVisualWebHost.OwnerId)
            .Select(p => (bool?)p.ConfirmRoutineActions).FirstOrDefaultAsync(ct) ?? true;
    }

    private Task<TenantDbContext> TenantDbAsync(CancellationToken ct) =>
        _host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(TenantVisualWebHost.TenantId, ct);

    private async Task AddFinishedReceiptAsync(Guid orderId, PrintJobStatus status, CancellationToken ct)
    {
        await using var db = await TenantDbAsync(ct);
        db.PrintJobs.Add(new PrintJob { OrderId = orderId, Type = PrintJobType.Receipt, Status = status, CopyCount = 1, PayloadJson = "{}", AttemptCount = 1 });
        await db.SaveChangesAsync(ct);
    }

    private async Task<int> ReceiptJobCountAsync(Guid orderId, CancellationToken ct)
    {
        await using var db = await TenantDbAsync(ct);
        return await db.PrintJobs.AsNoTracking().CountAsync(j => j.OrderId == orderId && j.Type == PrintJobType.Receipt, ct);
    }

    private async Task<OrderStatus> StatusAsync(Guid orderId, CancellationToken ct)
    {
        await using var db = await TenantDbAsync(ct);
        return await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.InternalStatus).SingleAsync(ct);
    }

    private async Task WaitStatusAsync(Guid orderId, OrderStatus expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await StatusAsync(orderId, ct) != expected && DateTime.UtcNow < deadline)
            await Task.Delay(100, ct);
        Assert.Equal(expected, await StatusAsync(orderId, ct));
    }
}
