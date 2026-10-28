using System.Text.Json;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Xunit;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The Live Screen order detail modal (approved Figma section 232:2324) in a real headless browser, served by
/// <see cref="TenantVisualWebHost"/> with the fictional orders of <see cref="OrderDetailModalSeed"/>: two columns where
/// only the products scroll, the full-screen phone layout with its sticky lifecycle bar, Arabic mirrored, long Russian
/// labels, the provider order time only (and its unknown state), keyboard focus, a lifecycle action submitted from the
/// modal, and the Focus view still showing its own detail. Same browser and CDN prerequisites as the other
/// headless-browser tests.
/// </summary>
[Trait("Category", "Browser")]
public sealed class OrderDetailModalBrowserTests : IAsyncLifetime
{
    private TenantVisualWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantVisualWebHost.StartAsync(seed: OrderDetailModalSeed.Add);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(768, 1024)]
    public async Task LongOrder_OnlyTheProductsScroll_AndNothingOverflowsSideways(int width, int height)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", width, height, "light", ct);
        await OpenAsync(browser, OrderDetailModalSeed.LongOrderId, ct);

        var before = await LayoutAsync(browser, ct);
        Assert.True(before.GetProperty("columns").GetInt32() == 2, $"{width}px: the body is not two columns: {before}");
        Assert.True(before.GetProperty("mainScrolls").GetBoolean(), $"{width}px: the long product list does not scroll: {before}");
        Assert.False(before.GetProperty("bodyScrolls").GetBoolean(), $"{width}px: the modal body scrolls instead of the products: {before}");
        Assert.True(before.GetProperty("sideways").GetInt32() <= 0, $"{width}px: horizontal overflow: {before}");

        await browser.EvaluateAsync<bool>("(() => { const m = document.querySelector('#ordersLiveDetailModal .wasla-live-detail__main'); m.scrollTop = m.scrollHeight; return true; })()", ct);
        var after = await LayoutAsync(browser, ct);
        Assert.True(after.GetProperty("mainScrollTop").GetDouble() > 0, $"{width}px: the product column did not scroll: {after}");
        foreach (var fixedPart in new[] { "headTop", "factsTop", "summaryTop", "actionsTop" })
            Assert.Equal(before.GetProperty(fixedPart).GetDouble(), after.GetProperty(fixedPart).GetDouble(), 0.5);

        // The summary in the side panel shows the authoritative fees and total; the totals under the products are hidden.
        Assert.True(after.GetProperty("summaryVisible").GetBoolean(), $"{width}px: the side panel summary is hidden: {after}");
        Assert.False(after.GetProperty("productTotalsVisible").GetBoolean(), $"{width}px: the totals under the products are visible: {after}");
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Phone_IsFullScreenOneColumn_AndTheStickyActionsLeaveTheLastContentVisible(string theme)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 390, 844, theme, ct);
        await OpenAsync(browser, OrderDetailModalSeed.StandardOrderId, ct);

        var layout = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const modal = document.getElementById('ordersLiveDetailModal');
              const dialog = modal.querySelector('.modal-dialog').getBoundingClientRect();
              const body = modal.querySelector('.modal-body');
              const bar = modal.querySelector('.wasla-orders-actions').getBoundingClientRect();
              const main = modal.querySelector('.wasla-live-detail__main').getBoundingClientRect();
              const facts = modal.querySelector('.wasla-live-detail__facts').getBoundingClientRect();
              const atTop = { barBottom: bar.bottom };
              body.scrollTop = body.scrollHeight;
              const end = modal.querySelector('.wasla-live-detail__open').getBoundingClientRect();
              const barAtEnd = modal.querySelector('.wasla-orders-actions').getBoundingClientRect();
              return {
                width: dialog.width, height: dialog.height, oneColumn: facts.top >= main.bottom - 1,
                barBottom: atTop.barBottom, lastBottom: end.bottom, barTopAtEnd: barAtEnd.top,
                sideways: document.documentElement.scrollWidth - window.innerWidth + (body.scrollWidth - body.clientWidth)
              };
            })()
            """, ct);
        Assert.Equal(390, layout.GetProperty("width").GetDouble(), 1);
        Assert.Equal(844, layout.GetProperty("height").GetDouble(), 1);
        Assert.True(layout.GetProperty("oneColumn").GetBoolean(), $"The phone layout is not one column: {layout}");
        Assert.Equal(844, layout.GetProperty("barBottom").GetDouble(), 1);
        Assert.True(layout.GetProperty("lastBottom").GetDouble() <= layout.GetProperty("barTopAtEnd").GetDouble() + 0.5,
            $"The sticky bar covers the last content: {layout}");
        Assert.True(layout.GetProperty("sideways").GetDouble() <= 0, $"Horizontal overflow at 390 px: {layout}");
    }

    [Fact]
    public async Task Arabic_MirrorsTheLayout_AndKeepsCodesAndPhonesLeftToRight()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("ar-SA", 1440, 900, "light", ct);
        await OpenAsync(browser, OrderDetailModalSeed.StandardOrderId, ct);

        var layout = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const m = document.getElementById('ordersLiveDetailModal');
              const r = s => m.querySelector(s).getBoundingClientRect();
              return {
                dir: document.documentElement.dir,
                panelLeftOfProducts: r('.wasla-live-detail__facts').right <= r('.wasla-live-detail__main').left + 1,
                closeLeftOfTitle: r('.btn-close').right <= r('.modal-title').left,
                platformRightOfCode: r('.wasla-live-detail__platform').left >= r('.wasla-live-detail__code').right - 1,
                lifecycleOnTheLeft: r('.wasla-orders-actions').right <= r('.wasla-order-print').left,
                codeDirection: getComputedStyle(m.querySelector('.wasla-live-detail__code')).direction,
                phoneDirection: m.querySelector('.wasla-live-detail__fact--phone dd').dir
              };
            })()
            """, ct);
        Assert.Equal("rtl", layout.GetProperty("dir").GetString());
        foreach (var check in new[] { "panelLeftOfProducts", "closeLeftOfTitle", "platformRightOfCode", "lifecycleOnTheLeft" })
            Assert.True(layout.GetProperty(check).GetBoolean(), $"Arabic is not mirrored ({check}): {layout}");
        Assert.Equal("ltr", layout.GetProperty("codeDirection").GetString());
        Assert.Equal("ltr", layout.GetProperty("phoneDirection").GetString());
    }

    [Theory]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task Russian_LongLabels_StayInsideTheirControls(int width, int height)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("ru-RU", width, height, "light", ct);
        foreach (var id in new[] { OrderDetailModalSeed.AcceptedOrderId, OrderDetailModalSeed.StandardOrderId })
        {
            await OpenAsync(browser, id, ct);
            var clipped = await browser.EvaluateAsync<string[]>("""
                [...document.querySelectorAll('#ordersLiveDetailModal .btn, #ordersLiveDetailModal dt, #ordersLiveDetailModal .wasla-live-detail__headline > *')]
                  .filter(e => e.getClientRects().length > 0 && getComputedStyle(e).position !== 'absolute')
                  .filter(e => e.scrollWidth > e.clientWidth + 1 || e.getBoundingClientRect().right > e.closest('.modal-content').getBoundingClientRect().right + 0.5)
                  .map(e => e.className + ': ' + e.textContent.trim())
                """, ct);
            Assert.True(clipped.Length == 0, $"{width}px: clipped or overflowing: {string.Join(" | ", clipped)}");
            await CloseAsync(browser, ct);
        }
    }

    [Fact]
    public async Task WithoutAProviderTime_ShowsTheUnknownStateAndNeverTheImportTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, "light", ct);
        await OpenAsync(browser, OrderDetailModalSeed.NoProviderTimeOrderId, ct);

        var state = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const m = document.getElementById('ordersLiveDetailModal');
              const visible = e => !!e && e.getClientRects().length > 0;
              return {
                unknown: visible(m.querySelector('[data-order-time-unknown]')) ? m.querySelector('[data-order-time-unknown]').textContent.trim() : null,
                received: visible(m.querySelector('[data-received]')),
                elapsed: visible(m.querySelector('[data-elapsed]')),
                text: m.querySelector('.modal-content').innerText
              };
            })()
            """, ct);
        Assert.Equal("Sipariş tarihi bilinmiyor", state.GetProperty("unknown").GetString());
        Assert.False(state.GetProperty("received").GetBoolean());
        Assert.False(state.GetProperty("elapsed").GetBoolean());
        Assert.DoesNotContain("Wasla'ya alındı", state.GetProperty("text").GetString(), StringComparison.Ordinal);

        await CloseAsync(browser, ct);
        await OpenAsync(browser, OrderDetailModalSeed.StandardOrderId, ct);
        Assert.True(await browser.EvaluateAsync<bool>(
            "(() => { const e = document.querySelector('#ordersLiveDetailModal [data-received]'); return e.getClientRects().length > 0 && e.textContent.includes('Sipariş zamanı'); })()", ct));
    }

    [Fact]
    public async Task Keyboard_TabStaysInTheDialog_AndEscapeReturnsFocusToTheOpener()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, "light", ct);
        await LoadLiveScreenAsync(browser, ct);
        var opener = $"#ordersLiveScreenHost [data-order-detail=\"{OrderDetailModalSeed.StandardOrderId}\"]";
        Assert.True(await browser.EvaluateAsync<bool>($"(() => {{ const e = document.querySelector({JsonSerializer.Serialize(opener)}); e.focus(); return document.activeElement === e; }})()", ct));
        await browser.PressKeyAsync("Enter", shift: false, ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('#ordersLiveDetailModal.show .wasla-live-detail__actions')", TimeSpan.FromSeconds(10), ct));
        Assert.True(await browser.EvaluateAsync<bool>("document.getElementById('ordersLiveDetailModal').getAttribute('aria-modal') === 'true'", ct));
        // Bootstrap moves focus into the dialog when its opening transition ends.
        Assert.True(await browser.WaitForAsync("document.getElementById('ordersLiveDetailModal').contains(document.activeElement)", TimeSpan.FromSeconds(5), ct),
            "Focus did not move into the dialog.");

        // Bootstrap's focus trap: after the last control focus may pass the browser's own position (the document body)
        // and comes back to the dialog on the next Tab; it never reaches a control behind the dialog.
        var insideCount = 0;
        for (var i = 0; i < 16; i++)
        {
            await browser.PressKeyAsync("Tab", shift: false, ct);
            var where = await browser.EvaluateAsync<string>(
                "document.getElementById('ordersLiveDetailModal').contains(document.activeElement) ? 'dialog' : (document.activeElement === document.body ? 'body' : document.activeElement.outerHTML.slice(0, 120))", ct);
            Assert.True(where is "dialog" or "body", $"Tab {i + 1} reached a control behind the dialog: {where}");
            if (where == "dialog") insideCount++;
            if (where == "dialog" && await browser.EvaluateAsync<bool>("document.activeElement.matches('.btn, .btn-close, a')", ct))
                Assert.True(await browser.EvaluateAsync<bool>(
                    "(() => { const s = getComputedStyle(document.activeElement); return (s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0) || s.boxShadow !== 'none'; })()", ct),
                    $"Tab {i + 1}: the focused control has no visible focus indicator.");
        }
        Assert.True(insideCount >= 12, $"Focus stayed in the dialog on only {insideCount} of 16 Tab presses.");

        await browser.PressKeyAsync("Escape", shift: false, ct);
        Assert.True(await browser.WaitForAsync("!document.querySelector('.modal.show, .modal-backdrop')", TimeSpan.FromSeconds(5), ct));
        Assert.True(await browser.WaitForAsync(
            $"document.activeElement && document.activeElement.getAttribute('data-order-detail') === '{OrderDetailModalSeed.StandardOrderId}'", TimeSpan.FromSeconds(5), ct),
            "Focus did not return to the order's details button.");
    }

    [Fact]
    public async Task Approve_InTheModal_SubmitsOnceAndShowsTheNextAction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, "light", ct);
        // The real confirmation stays in place; the test answers it and counts the lifecycle requests.
        await browser.AddScriptBeforePageScriptsAsync("""
            window.confirm = () => true;
            window.__orderActionPosts = 0;
            const realFetch = window.fetch.bind(window);
            window.fetch = (url, options) => {
              if (options && options.method === 'POST' && /\/orders\/[^/]+\/approve/.test(String(url))) window.__orderActionPosts++;
              return realFetch(url, options);
            };
            """, ct);
        await OpenAsync(browser, OrderDetailModalSeed.SecondNewOrderId, ct);

        Assert.True(await browser.EvaluateAsync<bool>("""
            (() => {
              const b = document.querySelector('#ordersLiveDetailModal [data-order-action="approve"]');
              b.click(); b.click();
              return true;
            })()
            """, ct));
        Assert.True(await browser.WaitForAsync(
            "!!document.querySelector('#ordersLiveDetailModal [data-order-action=\"start-preparing\"]')", TimeSpan.FromSeconds(15), ct),
            (_host.ServerErrors ?? "") + " The modal did not show Start preparing after Approve.");
        Assert.Equal(1, await browser.EvaluateAsync<int>("window.__orderActionPosts", ct));
        Assert.True(await browser.EvaluateAsync<bool>(
            "document.querySelector('#ordersLiveDetailModal .wasla-live-detail__open').getAttribute('href') === '/orders/details/" + OrderDetailModalSeed.SecondNewOrderId + "'", ct));
    }

    [Fact]
    public async Task FocusView_KeepsItsOwnDetail_WithoutTheModalOnlyParts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, "light", ct, view: "focus");
        await LoadLiveScreenAsync(browser, ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('#ordersLiveScreenHost .wasla-live-focus .wasla-live-detail__actions')", TimeSpan.FromSeconds(15), ct));

        var state = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const d = document.querySelector('#ordersLiveScreenHost .wasla-live-detail');
              const visible = s => [...d.querySelectorAll(s)].some(e => e.getClientRects().length > 0);
              return { modalOnly: visible('[data-detail-modal-only]'), totals: visible('.wasla-live-detail__totals'), received: visible('[data-received]') };
            })()
            """, ct);
        Assert.False(state.GetProperty("modalOnly").GetBoolean(), $"Focus shows modal-only parts: {state}");
        Assert.True(state.GetProperty("totals").GetBoolean(), $"Focus lost its totals: {state}");
        Assert.True(state.GetProperty("received").GetBoolean(), $"Focus lost its order time: {state}");
    }

    private static async Task<JsonElement> LayoutAsync(HeadlessChromium browser, CancellationToken ct) =>
        await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const m = document.getElementById('ordersLiveDetailModal');
              const q = s => m.querySelector(s);
              const top = s => q(s).getBoundingClientRect().top;
              const visible = s => q(s).getClientRects().length > 0;
              const main = q('.wasla-live-detail__main');
              const body = q('.modal-body');
              const content = q('.modal-content');
              return {
                columns: getComputedStyle(q('.wasla-live-detail__body')).gridTemplateColumns.split(' ').length,
                mainScrolls: main.scrollHeight > main.clientHeight + 1,
                mainScrollTop: main.scrollTop,
                bodyScrolls: body.scrollHeight > body.clientHeight + 1,
                sideways: Math.max(document.documentElement.scrollWidth - window.innerWidth, content.scrollWidth - content.clientWidth, main.scrollWidth - main.clientWidth),
                headTop: top('.wasla-live-detail__head'), factsTop: top('.wasla-live-detail__facts'),
                summaryTop: top('.wasla-live-detail__summary'), actionsTop: top('.wasla-live-detail__actions'),
                summaryVisible: visible('.wasla-live-detail__summary'), productTotalsVisible: visible('.wasla-live-detail__totals')
              };
            })()
            """, ct);

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, int height, string theme, CancellationToken ct, string view = "board")
    {
        var browser = await _host.BrowserAsync(culture, width, height, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the order detail modal checks.");
        return await browser.SetUpAsync(async started =>
        {
            await started.SetSystemColorSchemeAsync(theme, ct);
            await started.NavigateAsync(new Uri(_host.BaseAddress, "/css/site.css"), ct);
            await started.EvaluateAsync<bool>(
                $"(() => {{ localStorage.clear(); localStorage.setItem('Wasla.tenant.theme', '{theme}'); localStorage.setItem('Wasla.liveScreen.viewMode', '{view}'); return true; }})()", ct);
        });
    }

    private async Task LoadLiveScreenAsync(HeadlessChromium browser, CancellationToken ct)
    {
        try { await browser.NavigateAsync(new Uri(_host.BaseAddress, "/orders/live-display"), ct); }
        catch (TimeoutException) { await browser.NavigateAsync(new Uri(_host.BaseAddress, "/orders/live-display"), ct); }
        Assert.True(await browser.WaitForAsync("!!document.querySelector('#ordersLiveScreenHost [data-order-id]')", TimeSpan.FromSeconds(15), ct),
            (_host.ServerErrors ?? "") + " The Live Screen did not render the seeded orders.");
    }

    private async Task OpenAsync(HeadlessChromium browser, Guid orderId, CancellationToken ct)
    {
        if (!await browser.EvaluateAsync<bool>("!!(window.WaslaOrders && window.WaslaOrders.liveDetailModal)", ct))
            await LoadLiveScreenAsync(browser, ct);
        await browser.EvaluateAsync<bool>($"(() => {{ window.WaslaOrders.liveDetailModal.open('{orderId}', null); return true; }})()", ct);
        Assert.True(await browser.WaitForAsync(
            $"!!document.querySelector('#ordersLiveDetailModal.show .wasla-live-detail[data-order-id=\"{orderId}\"]') && !document.querySelector('#ordersLiveDetailModal.fade:not(.show)')",
            TimeSpan.FromSeconds(10), ct), (_host.ServerErrors ?? "") + $" The detail modal did not open for {orderId}.");
        await Task.Delay(350, ct);
    }

    private static async Task CloseAsync(HeadlessChromium browser, CancellationToken ct)
    {
        await browser.PressKeyAsync("Escape", shift: false, ct);
        Assert.True(await browser.WaitForAsync("!document.querySelector('.modal.show, .modal-backdrop')", TimeSpan.FromSeconds(5), ct));
    }
}

/// <summary>Fictional orders for the order detail modal checks and screenshots: no real customer data.</summary>
internal static class OrderDetailModalSeed
{
    public static readonly Guid StandardOrderId = Guid.Parse("82000000-0000-4000-8000-0000000000d1");
    public static readonly Guid LongOrderId = Guid.Parse("82000000-0000-4000-8000-0000000000d2");
    public static readonly Guid AcceptedOrderId = Guid.Parse("82000000-0000-4000-8000-0000000000d3");
    public static readonly Guid NoProviderTimeOrderId = Guid.Parse("82000000-0000-4000-8000-0000000000d4");
    public static readonly Guid SecondNewOrderId = Guid.Parse("82000000-0000-4000-8000-0000000000d5");

    public static void Add(TenantDbContext db, DateTime now)
    {
        db.Orders.AddRange(
            Order(StandardOrderId, FoodPlatform.GetirYemek, "GY-58217", OrderStatus.New, now.AddMinutes(-6), providerTime: true,
                "Elif Demir", "+90 500 000 00 00", "Caferağa Mah. Örnek Sok. No: 12 D: 4, Kadıköy / İstanbul",
                "Zili çalmayın, bebek uyuyor. Kapıya bırakabilirsiniz.", PaymentMethod.OnlinePayment, deliveryFee: 0m, serviceFee: 5m, total: 940m,
                Item("Izgara Köfte Porsiyon", 1, 320m, null, ("Ekstra pilav", 40m)),
                Item("Tavuk Sote", 1, 275m, "Acısız olsun, lütfen."),
                Item("Mercimek Çorbası", 1, 110m, null),
                Item("Ayran", 2, 45m, null),
                Item("Fırın Sütlaç", 1, 140m, null)),
            Order(LongOrderId, FoodPlatform.TrendyolYemek, "TY-48213907", OrderStatus.Preparing, now.AddMinutes(-14), providerTime: true,
                "Kerem Aksoy", "+90 500 000 00 01", "Barbaros Mah. Örnek Cad. No: 48 Kat: 7, Ataşehir / İstanbul",
                "Ofis siparişi. Her menüyü ayrı paketleyip üzerine isim yazar mısınız? Peçete ve çatal-bıçak eklemeyin.",
                PaymentMethod.OnlinePayment, deliveryFee: 29.90m, serviceFee: 5m, total: 3509.90m,
                Item("Adana Dürüm", 3, 235m, "Biri soğansız olsun.", ("Acılı", 0m), ("Közlenmiş biber", 15m), ("Ayran yerine şalgam", 10m)),
                Item("Tavuk Şiş Porsiyon", 2, 290m, null, ("Bulgur pilavı", 0m), ("Ekstra lavaş", 10m)),
                Item("Karışık Izgara (2 kişilik)", 1, 845m, "Etler iyi pişmiş olsun.", ("Pilav yerine patates", 0m), ("Ekstra köz domates", 20m), ("Acı ezme", 25m)),
                Item("Lahmacun", 1, 110m, "Bol limon ve maydanoz."),
                Item("Mercimek Çorbası", 2, 125m, null, ("Kıtır ekmek", 15m)),
                Item("Çoban Salata", 1, 120m, null, ("Sos ayrı", 0m)),
                Item("İçli Köfte (2 adet)", 1, 180m, null),
                Item("Künefe", 1, 290m, "Sıcak gönderilsin.", ("Kaymaklı", 30m), ("Antep fıstığı", 40m)),
                Item("Sütlaç", 2, 130m, null),
                Item("Şalgam (Acılı)", 1, 55m, null),
                Item("Su (0,5 L)", 1, 20m, null),
                Item("Kola (Kutu)", 1, 60m, null)),
            Order(AcceptedOrderId, FoodPlatform.Yemeksepeti, "YS-30418", OrderStatus.Accepted, now.AddMinutes(-3), providerTime: true,
                "Mert Kaya", "+90 500 000 00 02", "Moda Mah. Örnek Sok. No: 3, Kadıköy / İstanbul", null,
                PaymentMethod.Cash, deliveryFee: 15m, serviceFee: 5m, total: 955m,
                Item("Izgara Köfte Porsiyon", 1, 320m, null, ("Ekstra pilav", 40m)),
                Item("Tavuk Sote", 1, 275m, null),
                Item("Ayran", 2, 45m, null),
                Item("Fırın Sütlaç", 1, 140m, null)),
            Order(NoProviderTimeOrderId, FoodPlatform.GetirYemek, "GY-58301", OrderStatus.New, now.AddMinutes(-4), providerTime: false,
                "Ayla Tekin", "+90 500 000 00 03", "Fenerbahçe Mah. Örnek Sok. No: 7, Kadıköy / İstanbul", null,
                PaymentMethod.CreditCard, deliveryFee: 0m, serviceFee: 0m, total: 385m,
                Item("Tavuk Sote", 1, 275m, null),
                Item("Mercimek Çorbası", 1, 110m, null)),
            Order(SecondNewOrderId, FoodPlatform.Yemeksepeti, "YS-30422", OrderStatus.New, now.AddMinutes(-1), providerTime: true,
                "Can Yıldız", "+90 500 000 00 04", "Rasimpaşa Mah. Örnek Sok. No: 9, Kadıköy / İstanbul", null,
                PaymentMethod.OnlinePayment, deliveryFee: 0m, serviceFee: 0m, total: 275m,
                Item("Tavuk Sote", 1, 275m, null)));
    }

    private static Order Order(Guid id, FoodPlatform platform, string code, OrderStatus status, DateTime receivedAt, bool providerTime,
        string customer, string phone, string address, string? note, PaymentMethod payment, decimal deliveryFee, decimal serviceFee,
        decimal total, params OrderItem[] items) => new()
    {
        Id = id,
        Platform = platform,
        ExternalOrderId = code,
        ExternalOrderCode = code,
        IdempotencyKey = $"{platform}:{code}",
        InternalStatus = status,
        PlatformStatus = status.ToString(),
        CustomerName = customer,
        CustomerPhone = phone,
        CustomerAddress = address,
        CustomerNote = note,
        TotalAmount = total,
        DeliveryFee = deliveryFee,
        ServiceFee = serviceFee,
        PaymentMethod = payment,
        PaymentStatus = payment == PaymentMethod.Cash ? PaymentStatus.Pending : PaymentStatus.Paid,
        CreatedAtPlatform = providerTime ? receivedAt : null,
        ReceivedAt = receivedAt,
        AcceptedAt = status is OrderStatus.New ? null : receivedAt.AddMinutes(1),
        RawPayloadJson = "{}",
        Items = items.Select((item, index) => { item.Sequence = index; return item; }).ToList()
    };

    private static OrderItem Item(string name, int quantity, decimal unitPrice, string? notes, params (string Name, decimal Price)[] options) => new()
    {
        ProductName = name,
        Quantity = quantity,
        UnitPrice = unitPrice,
        TotalPrice = unitPrice * quantity,
        Notes = notes,
        Options = options.Select((o, index) => new OrderItemOption { Name = o.Name, Price = o.Price, Sequence = index }).ToList()
    };
}
