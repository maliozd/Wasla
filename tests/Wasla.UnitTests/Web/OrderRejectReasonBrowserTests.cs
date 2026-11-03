using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Xunit;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Rejecting a Trendyol GO order asks for one of its restaurant reasons in a real headless browser, on every screen that
/// offers Reject: the Live Screen board, the Live Screen detail modal and the Orders page. Nothing is preselected, a
/// confirmation without a reason posts nothing, and the chosen reason is the one posted. A Yemeksepeti order keeps its
/// plain confirmation and posts no reason. Served by <see cref="TenantVisualWebHost"/> in Mock provider mode with
/// fictional orders; same browser and CDN prerequisites as the other headless-browser tests.
/// </summary>
[Trait("Category", "Browser")]
public sealed class OrderRejectReasonBrowserTests : IAsyncLifetime
{
    private static readonly Guid TrendyolOrderId = Guid.Parse("83000000-0000-4000-8000-0000000000e1");

    // Counts every order action POST with its body; the real request still goes to the server.
    private const string RecordPosts = """
        window.__rejectPosts = [];
        window.__confirms = 0;
        window.confirm = () => { window.__confirms++; return true; };
        const realFetch = window.fetch.bind(window);
        window.fetch = (url, options) => {
          if (options && options.method === 'POST' && /\/orders\/[^/]+\/reject$/.test(String(url)))
            window.__rejectPosts.push({ url: String(url), body: options.body === undefined ? null : String(options.body) });
          return realFetch(url, options);
        };
        """;

    private TenantVisualWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantVisualWebHost.StartAsync(seed: AddTrendyolOrder);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task LiveScreen_TrendyolGo_AsksForAReason_AndPostsTheChosenOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadAsync(browser, "/orders/live-display", "#ordersLiveScreenHost", ct);

        await ClickRejectAsync(browser, "#ordersLiveScreenHost", TrendyolOrderId, ct);
        var dialog = await DialogAsync(browser, ct);
        Assert.Equal(["621", "622", "623", "627"], dialog.GetProperty("values").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(0, dialog.GetProperty("checked").GetInt32());
        Assert.Equal(["Tedarik problemi", "Mağaza kapalı", "Mağaza siparişi hazırlayamıyor", "Sipariş karışıklığı"],
            dialog.GetProperty("labels").EnumerateArray().Select(v => v.GetString()));
        Assert.True(dialog.GetProperty("focusedReason").GetBoolean(), $"Focus is not on the first reason: {dialog}");

        // Confirming without a reason posts nothing and says why.
        await ConfirmAsync(browser, ct);
        Assert.True(await browser.WaitForAsync("!document.querySelector('[data-order-reject-error]').hidden", TimeSpan.FromSeconds(5), ct));
        Assert.Equal("Siparişi reddetmek için bir neden seçin.", await browser.EvaluateAsync<string>("document.querySelector('[data-order-reject-error]').textContent.trim()", ct));
        Assert.True(await browser.EvaluateAsync<bool>("document.querySelector('#ordersRejectDialog.show') !== null && window.__rejectPosts.length === 0", ct));

        await ChooseAsync(browser, 622, ct);
        Assert.True(await browser.EvaluateAsync<bool>("document.querySelector('[data-order-reject-error]').hidden", ct), "The error stayed after choosing a reason.");
        await ConfirmAsync(browser, ct);

        await AssertRejectedAsync(browser, TrendyolOrderId, "reasonId=622", ct);
        Assert.Equal(0, await browser.EvaluateAsync<int>("window.__confirms", ct));
    }

    [Fact]
    public async Task LiveScreen_DetailModal_ClosesForTheDialog_AndCancelChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadAsync(browser, "/orders/live-display", "#ordersLiveScreenHost", ct);
        await browser.EvaluateAsync<bool>($"(() => {{ window.WaslaOrders.liveDetailModal.open('{TrendyolOrderId}', null); return true; }})()", ct);
        Assert.True(await browser.WaitForAsync(
            $"!!document.querySelector('#ordersLiveDetailModal.show [data-order-action=\"reject\"][data-order-id=\"{TrendyolOrderId}\"]')", TimeSpan.FromSeconds(10), ct),
            (_host.ServerErrors ?? "") + " The detail modal did not show Reject.");
        await Task.Delay(350, ct);

        await ClickRejectAsync(browser, "#ordersLiveDetailModal", TrendyolOrderId, ct);
        await DialogAsync(browser, ct);
        Assert.False(await browser.EvaluateAsync<bool>("!!document.querySelector('#ordersLiveDetailModal.show')", ct), "The detail modal stayed open under the dialog.");
        Assert.Equal(1, await browser.EvaluateAsync<int>("document.querySelectorAll('.modal-backdrop').length", ct));

        await ChooseAsync(browser, 627, ct);
        await browser.PressKeyAsync("Escape", shift: false, ct);
        Assert.True(await browser.WaitForAsync("!document.querySelector('.modal.show, .modal-backdrop')", TimeSpan.FromSeconds(5), ct));
        Assert.True(await browser.WaitForAsync(
            $"document.activeElement && document.activeElement.getAttribute('data-order-detail') === '{TrendyolOrderId}'", TimeSpan.FromSeconds(5), ct),
            "Focus did not return to the order's details button.");
        await Task.Delay(500, ct);
        Assert.Equal(0, await browser.EvaluateAsync<int>("window.__rejectPosts.length", ct));
        Assert.Equal(OrderStatus.New, await StatusAsync(TrendyolOrderId, ct));

        // Opening it again preselects nothing, even after a reason was chosen and cancelled.
        await ClickRejectAsync(browser, "#ordersLiveScreenHost", TrendyolOrderId, ct);
        Assert.Equal(0, (await DialogAsync(browser, ct)).GetProperty("checked").GetInt32());
    }

    [Fact]
    public async Task OrdersPage_TrendyolGo_AsksForAReason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadAsync(browser, "/orders", "#ordersTableHost", ct);

        await ClickRejectAsync(browser, "#ordersTableHost", TrendyolOrderId, ct);
        await DialogAsync(browser, ct);
        await ChooseAsync(browser, 627, ct);
        await ConfirmAsync(browser, ct);

        await AssertRejectedAsync(browser, TrendyolOrderId, "reasonId=627", ct);
    }

    /// <summary>Yemeksepeti has no verified reason list: its Reject keeps the plain confirmation and posts no reason.</summary>
    [Fact]
    public async Task LiveScreen_Yemeksepeti_KeepsThePlainConfirmation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, ct);
        await LoadAsync(browser, "/orders/live-display", "#ordersLiveScreenHost", ct);

        await ClickRejectAsync(browser, "#ordersLiveScreenHost", TenantVisualWebHost.DetailOrderId, ct);

        await AssertRejectedAsync(browser, TenantVisualWebHost.DetailOrderId, expectedBody: null, ct);
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__confirms", ct));
        Assert.False(await browser.EvaluateAsync<bool>("!!document.querySelector('#ordersRejectDialog.show')", ct), "The reason dialog opened for Yemeksepeti.");
    }

    [Fact]
    public async Task Arabic_Phone_TheDialogIsMirroredAndFits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("ar-SA", 390, 844, ct);
        await LoadAsync(browser, "/orders/live-display", "#ordersLiveScreenHost", ct);

        await ClickRejectAsync(browser, "#ordersLiveScreenHost", TrendyolOrderId, ct);
        var dialog = await DialogAsync(browser, ct);
        Assert.Equal(["مشكلة في التوريد", "المتجر مغلق", "لا يستطيع المتجر تحضير الطلب", "التباس في الطلب"],
            dialog.GetProperty("labels").EnumerateArray().Select(v => v.GetString()));

        var layout = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const d = document.querySelector('#ordersRejectDialog .modal-content').getBoundingClientRect();
              const radio = document.querySelector('#ordersRejectDialog fieldset:not([hidden]) .form-check-input').getBoundingClientRect();
              const label = document.querySelector('#ordersRejectDialog fieldset:not([hidden]) .form-check-label').getBoundingClientRect();
              return {
                dir: getComputedStyle(document.querySelector('#ordersRejectDialog .modal-body')).direction,
                left: d.left, right: d.right, width: window.innerWidth,
                radioRightOfLabel: radio.left >= label.right - 1,
                sideways: document.documentElement.scrollWidth - window.innerWidth
              };
            })()
            """, ct);
        Assert.Equal("rtl", layout.GetProperty("dir").GetString());
        Assert.True(layout.GetProperty("left").GetDouble() >= 0 && layout.GetProperty("right").GetDouble() <= layout.GetProperty("width").GetDouble(), $"The dialog does not fit: {layout}");
        Assert.True(layout.GetProperty("radioRightOfLabel").GetBoolean(), $"The reasons are not mirrored: {layout}");
        Assert.True(layout.GetProperty("sideways").GetDouble() <= 0, $"Horizontal overflow: {layout}");

        await ConfirmAsync(browser, ct);
        Assert.Equal("اختر سببًا لرفض الطلب.", await browser.EvaluateAsync<string>("document.querySelector('[data-order-reject-error]').textContent.trim()", ct));
    }

    private static void AddTrendyolOrder(TenantDbContext db, DateTime now) =>
        db.Orders.Add(new Order
        {
            Id = TrendyolOrderId,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = "TY-91005",
            ExternalOrderCode = "TY-91005",
            IdempotencyKey = "TrendyolYemek:TY-91005",
            InternalStatus = OrderStatus.New,
            PlatformStatus = "Created",
            CustomerName = "Derya Polat",
            CustomerPhone = "+90 500 000 00 09",
            CustomerAddress = "Örnek Mah. Deneme Sok. No: 5, Kadıköy / İstanbul",
            TotalAmount = 210m,
            PaymentMethod = PaymentMethod.OnlinePayment,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = now.AddMinutes(-1),
            ReceivedAt = now.AddMinutes(-1),
            RawPayloadJson = "{}",
            Items = [new OrderItem { ProductName = "Mercimek Çorbası", Quantity = 2, UnitPrice = 105m, TotalPrice = 210m }]
        });

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, int height, CancellationToken ct)
    {
        var browser = await _host.BrowserAsync(culture, width, height, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the reject reason checks.");
        return await browser.SetUpAsync(async started =>
        {
            await started.NavigateAsync(new Uri(_host.BaseAddress, "/css/site.css"), ct);
            await started.EvaluateAsync<bool>("(() => { localStorage.clear(); localStorage.setItem('Wasla.liveScreen.viewMode', 'board'); return true; })()", ct);
            await started.AddScriptBeforePageScriptsAsync(RecordPosts, ct);
        });
    }

    private async Task LoadAsync(HeadlessChromium browser, string path, string host, CancellationToken ct)
    {
        try { await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct); }
        catch (TimeoutException) { await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct); }
        Assert.True(await browser.WaitForAsync(
            $"!!document.querySelector('{host} [data-order-action=\"reject\"][data-order-id=\"{TrendyolOrderId}\"]')", TimeSpan.FromSeconds(15), ct),
            (_host.ServerErrors ?? "") + $" {path} did not render the Trendyol GO order's Reject.");
    }

    private static async Task ClickRejectAsync(HeadlessChromium browser, string host, Guid orderId, CancellationToken ct) =>
        Assert.True(await browser.EvaluateAsync<bool>(
            $"(() => {{ const b = document.querySelector('{host} [data-order-action=\"reject\"][data-order-id=\"{orderId}\"]'); if (!b) return false; b.click(); return true; }})()", ct),
            $"No Reject for {orderId} in {host}.");

    private static async Task<JsonElement> DialogAsync(HeadlessChromium browser, CancellationToken ct)
    {
        Assert.True(await browser.WaitForAsync(
            "!!document.querySelector('#ordersRejectDialog.show') && !document.querySelector('#ordersRejectDialog.fade:not(.show)') && document.activeElement && document.activeElement.name === 'reasonId'",
            TimeSpan.FromSeconds(5), ct), "The reject reason dialog did not open.");
        return await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const groups = [...document.querySelectorAll('#ordersRejectDialog [data-order-reject-reasons]')].filter(g => !g.hidden);
              const inputs = groups.length === 1 ? [...groups[0].querySelectorAll('input[name="reasonId"]')] : [];
              return {
                groups: groups.length,
                values: inputs.map(i => i.value),
                labels: inputs.map(i => document.querySelector(`label[for="${i.id}"]`).textContent.trim()),
                checked: inputs.filter(i => i.checked).length,
                focusedReason: document.activeElement === inputs[0]
              };
            })()
            """, ct);
    }

    private static async Task ChooseAsync(HeadlessChromium browser, int reasonId, CancellationToken ct) =>
        Assert.True(await browser.EvaluateAsync<bool>(
            $"(() => {{ const i = document.querySelector('#ordersRejectDialog fieldset:not([hidden]) input[value=\"{reasonId}\"]'); if (!i) return false; document.querySelector(`label[for=\"${{i.id}}\"]`).click(); return i.checked; }})()", ct),
            $"Reason {reasonId} could not be chosen.");

    private static async Task ConfirmAsync(HeadlessChromium browser, CancellationToken ct) =>
        Assert.True(await browser.EvaluateAsync<bool>("(() => { document.querySelector('#ordersRejectDialog [data-order-reject-confirm]').click(); return true; })()", ct));

    private async Task AssertRejectedAsync(HeadlessChromium browser, Guid orderId, string? expectedBody, CancellationToken ct)
    {
        Assert.True(await browser.WaitForAsync("window.__rejectPosts.length > 0", TimeSpan.FromSeconds(10), ct), "No reject was posted.");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await StatusAsync(orderId, ct) != OrderStatus.Cancelled && DateTime.UtcNow < deadline)
            await Task.Delay(100, ct);
        Assert.Equal(OrderStatus.Cancelled, await StatusAsync(orderId, ct));

        var posts = await browser.EvaluateAsync<JsonElement>("window.__rejectPosts", ct);
        var post = Assert.Single(posts.EnumerateArray());
        Assert.EndsWith($"/orders/{orderId}/reject", post.GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.Equal(expectedBody, post.GetProperty("body").ValueKind == JsonValueKind.Null ? null : post.GetProperty("body").GetString());
    }

    private async Task<OrderStatus> StatusAsync(Guid orderId, CancellationToken ct)
    {
        await using var db = await _host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(TenantVisualWebHost.TenantId, ct);
        return await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.InternalStatus).SingleAsync(ct);
    }
}
