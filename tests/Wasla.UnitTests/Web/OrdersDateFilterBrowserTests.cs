using System.Text.Json;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-130 in a real headless Chromium browser against <see cref="TenantVisualWebHost"/> with fixed orders around Turkey's
/// midnight (<see cref="OrdersDateFilterTests"/>): the browser reads the Orders date inputs as Gregorian days, a date chosen
/// in the input and applied with the Filter button comes back in the inputs and selects the same orders in every
/// language, the filter stays usable right to left at phone and desktop widths, and the Live Screen formats an earlier
/// day's order in the Gregorian calendar whatever the browser's own default for the language. Skips only when no Chromium
/// browser is installed or Bootstrap cannot be loaded from its CDN.
/// </summary>
public sealed class OrdersDateFilterBrowserTests : IAsyncLifetime
{
    private static readonly Guid ActiveOrderId = Guid.Parse("13000000-0000-4000-8000-0000000000a5");
    private const string ActiveOrderCode = "LIVE130-ACTIVE";
    // 00:00 on 10 March 2025 in Turkey (10 Ramadan 1446 in the Umm al-Qura calendar).
    private const string ActiveOrderUtc = "2025-03-09T21:00:00.000Z";

    private TenantVisualWebHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await TenantVisualWebHost.StartAsync(seed: (db, _) =>
        {
            OrdersDateFilterTests.SeedFixedOrders(db);
            db.Orders.Add(ActiveOrder());
        });

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("ar-SA", "rtl", 390)]
    [InlineData("ar-SA", "rtl", 1280)]
    [InlineData("tr-TR", "ltr", 1280)]
    [InlineData("ru-RU", "ltr", 390)]
    public async Task ChosenDates_RoundTripThroughTheFilter_AndSelectTheSameOrders(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await _host.BrowserAsync(culture, width, 860, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser Orders checks.");
        await LoadAsync(browser, "/orders?startDate=2025-03-10&endDate=2025-03-11", ct);

        var first = await FilterStateAsync(browser, ct);
        Assert.Equal("2025-03-10", first.GetProperty("start").GetString());
        Assert.Equal("2025-03-11", first.GetProperty("end").GetString());
        // The browser itself reads the values as Gregorian days (midnight UTC of that day).
        Assert.True(first.GetProperty("startIsGregorian").GetBoolean(), $"{culture}: the start input is not 10 March 2025");
        Assert.True(first.GetProperty("endIsGregorian").GetBoolean(), $"{culture}: the end input is not 11 March 2025");
        Assert.Equal("W130-FIRST,W130-LAST", first.GetProperty("listed").GetString());
        AssertLayout(first, culture, direction, width);

        // As the date picker does: a new day in the input, then a real click on Filter submits the form.
        await browser.EvaluateAsync<bool>("""
            (() => {
              const input = document.getElementById('ordersStartDate');
              input.value = '2025-03-11';
              input.dispatchEvent(new Event('input', { bubbles: true }));
              input.dispatchEvent(new Event('change', { bubbles: true }));
              document.querySelector('#ordersFilterForm button[type="submit"]').scrollIntoView({ block: 'center', behavior: 'instant' });
              return true;
            })()
            """, ct);
        // Measured after the scroll has settled, and only clicked where the Filter button itself receives the click.
        var button = await browser.EvaluateAsync<JsonElement>("""
            new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => {
              const submit = document.querySelector('#ordersFilterForm button[type="submit"]');
              const r = submit.getBoundingClientRect();
              const x = r.left + r.width / 2, y = r.top + r.height / 2;
              const hit = document.elementFromPoint(x, y);
              resolve({ x, y, onButton: !!hit && hit.closest('button') === submit });
            })))
            """, ct);
        Assert.True(button.GetProperty("onButton").GetBoolean(), $"{culture} {width}px: the Filter button is covered at its centre");
        await browser.ClickAtAsync(button.GetProperty("x").GetDouble(), button.GetProperty("y").GetDouble(), ct);
        Assert.True(await WaitAfterNavigationAsync(browser,
                "location.search.includes('startDate=2025-03-11') && document.readyState === 'complete' && !!document.getElementById('ordersStartDate')", ct),
            $"{culture}: the Filter button did not load the chosen range. {_host.ServerErrors}");

        var applied = await FilterStateAsync(browser, ct);
        Assert.Equal("2025-03-11", applied.GetProperty("start").GetString());
        Assert.Equal("2025-03-11", applied.GetProperty("end").GetString());
        Assert.Contains("endDate=2025-03-11", applied.GetProperty("search").GetString(), StringComparison.Ordinal);
        Assert.Equal("W130-LAST", applied.GetProperty("listed").GetString());
        Assert.Equal(0, applied.GetProperty("errors").GetInt32());
        AssertLayout(applied, culture, direction, width);
        Assert.Null(_host.ServerErrors);
    }

    [Theory]
    [InlineData("ar-SA", "rtl", 390)]
    [InlineData("ar-SA", "rtl", 1280)]
    public async Task InvertedRange_ShowsTheLocalizedError_NextToTheInputs_RightToLeft(string culture, string direction, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await _host.BrowserAsync(culture, width, 860, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser Orders checks.");
        await LoadAsync(browser, "/orders?startDate=2025-03-11&endDate=2025-03-10", ct);

        var state = await FilterStateAsync(browser, ct);
        Assert.Equal("2025-03-11", state.GetProperty("start").GetString());
        Assert.Equal("2025-03-10", state.GetProperty("end").GetString());
        Assert.Equal("", state.GetProperty("listed").GetString());
        Assert.Equal(OrdersDateFilterTests.Resource(culture, "Orders.InvalidDateRange"), state.GetProperty("errorText").GetString());
        Assert.True(state.GetProperty("errorShown").GetBoolean(), $"{culture} {width}px: the error is not visible");
        Assert.Equal("true", state.GetProperty("endInvalid").GetString());
        Assert.Equal("ordersFilterErrors", state.GetProperty("endDescribedBy").GetString());
        AssertLayout(state, culture, direction, width);
        Assert.Null(_host.ServerErrors);
    }

    [Theory]
    [InlineData("ar-SA", 390)]
    [InlineData("ar-SA", 1280)]
    [InlineData("en-US", 1280)]
    public async Task LiveScreen_ShowsAnEarlierDaysOrderInTheGregorianCalendar(string culture, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await _host.BrowserAsync(culture, width, 860, mobile: width < 992, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser Live Screen checks.");
        await LoadAsync(browser, "/orders/live-display", ct);
        Assert.True(await browser.WaitForAsync($"!!document.querySelector('#ordersLiveScreenHost [data-order-id=\"{ActiveOrderId}\"]')", TimeSpan.FromSeconds(15), ct),
            $"The Live Screen did not render {ActiveOrderCode}. {_host.ServerErrors}");
        await browser.EvaluateAsync<bool>("(() => { WaslaOrders.liveView.setView('list', { animate: false, persist: false }); return true; })()", ct);

        var shown = await browser.EvaluateAsync<JsonElement>($$"""
            (() => {
              const card = document.querySelector('#ordersLiveScreenHost [data-order-id="{{ActiveOrderId}}"]');
              const received = card && card.querySelector('[data-received]');
              const at = new Date('{{ActiveOrderUtc}}');
              const options = { timeZone: 'Europe/Istanbul', dateStyle: 'short', timeStyle: 'short' };
              const format = calendar => new Intl.DateTimeFormat('{{culture}}', Object.assign({ calendar }, options)).format(at);
              return {
                text: received ? received.textContent.trim() : null,
                gregorian: format('gregory'),
                hijri: format('islamic-umalqura'),
                browserDefault: new Intl.DateTimeFormat('{{culture}}').resolvedOptions().calendar,
                dir: document.documentElement.getAttribute('dir'),
                overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth
              };
            })()
            """, ct);

        var text = shown.GetProperty("text").GetString();
        Assert.Equal(shown.GetProperty("gregorian").GetString(), text);
        Assert.NotEqual(shown.GetProperty("hijri").GetString(), text);
        Assert.Equal(culture == "ar-SA" ? "rtl" : "ltr", shown.GetProperty("dir").GetString());
        Assert.False(shown.GetProperty("overflow").GetBoolean(), $"{culture} {width}px: the Live Screen overflows horizontally");
        TestContext.Current.TestOutputHelper?.WriteLine($"{culture}: browser default calendar {shown.GetProperty("browserDefault").GetString()}, shown '{text}'");
        Assert.Null(_host.ServerErrors);
    }

    private static async Task<JsonElement> FilterStateAsync(HeadlessChromium browser, CancellationToken ct) =>
        await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const start = document.getElementById('ordersStartDate');
              const end = document.getElementById('ordersEndDate');
              const errors = document.getElementById('ordersFilterErrors');
              const inView = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.left >= 0 && r.right <= document.documentElement.clientWidth; };
              const codes = [...document.querySelectorAll('#ordersTableContainer tr[data-order-id] a, tr[data-order-id] a')]
                .map(a => a.textContent.trim()).filter(t => t.startsWith('W130-'));
              return {
                start: start.value,
                end: end.value,
                startIsGregorian: start.valueAsNumber === Date.UTC(2025, 2, 10),
                endIsGregorian: end.valueAsNumber === Date.UTC(2025, 2, 11),
                search: location.search,
                listed: [...new Set(codes)].sort().join(','),
                errors: errors ? errors.children.length : 0,
                errorText: errors ? errors.textContent.trim() : null,
                errorShown: !!errors && inView(errors),
                endInvalid: end.getAttribute('aria-invalid'),
                endDescribedBy: end.getAttribute('aria-describedby'),
                inputsInView: inView(start) && inView(end),
                dir: document.documentElement.getAttribute('dir'),
                overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth
              };
            })()
            """, ct);

    private static void AssertLayout(JsonElement state, string culture, string direction, int width)
    {
        Assert.Equal(direction, state.GetProperty("dir").GetString());
        Assert.False(state.GetProperty("overflow").GetBoolean(), $"{culture} {width}px: Orders overflows horizontally");
        Assert.True(state.GetProperty("inputsInView").GetBoolean(), $"{culture} {width}px: a date input leaves the viewport");
    }

    /// <summary>Polls through a navigation: while the old document goes away an evaluation can fail, which is not an answer.</summary>
    private static async Task<bool> WaitAfterNavigationAsync(HeadlessChromium browser, string expression, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await browser.EvaluateAsync<bool>(expression, ct))
                    return true;
            }
            catch (InvalidOperationException)
            {
                // The execution context was replaced by the next document.
            }

            await Task.Delay(100, ct);
        }

        return false;
    }

    private async Task LoadAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        // The layout loads Bootstrap from its CDN; one retry absorbs a slow CDN response.
        try
        {
            await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        }
        catch (TimeoutException)
        {
            await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        }

        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
    }

    private static Order ActiveOrder()
    {
        var at = DateTime.Parse(ActiveOrderUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
        return new Order
        {
            Id = ActiveOrderId,
            Platform = FoodPlatform.Yemeksepeti,
            ExternalOrderId = ActiveOrderCode,
            ExternalOrderCode = ActiveOrderCode,
            IdempotencyKey = $"{FoodPlatform.Yemeksepeti}:{ActiveOrderCode}",
            InternalStatus = OrderStatus.Accepted,
            PlatformStatus = "Accepted",
            CustomerName = "Takvim Testi",
            TotalAmount = 95m,
            PaymentMethod = PaymentMethod.OnlinePayment,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = at,
            ReceivedAt = at,
            AcceptedAt = at.AddMinutes(1),
            CreatedAt = at,
            UpdatedAt = at,
            RawPayloadJson = "{}",
            Items = [new OrderItem { ProductName = "Pide", Quantity = 1, UnitPrice = 95m, TotalPrice = 95m }]
        };
    }
}
