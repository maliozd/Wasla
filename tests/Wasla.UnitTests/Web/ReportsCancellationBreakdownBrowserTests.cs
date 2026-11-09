using System.Globalization;
using System.Text.Json;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-200: the İptaller section's split and reasons in a real headless browser, over the real report service and fictional
/// orders: exact figures, the platform filter in place, no cancellations, all reasons unknown, mixed coverage, keyboard
/// access, and layout in every culture at desktop and phone widths with the longest labels.
/// </summary>
[Trait("Category", "Browser")]
public sealed class ReportsCancellationBreakdownBrowserTests : IAsyncLifetime
{
    private const string Result = "document.querySelector('#rptResult [data-rpt-result-root]')";

    private TenantVisualWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantVisualWebHost.StartAsync(seed: SeedEndings);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    /// <summary>A fixed local day; the standard seed has no orders this far back.</summary>
    private static string Day(int daysAgo) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).AddDays(-daysAgo).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private const int MixedDay = 4;
    private const int UnknownDay = 6;
    private const int NoCancellationDay = 8;

    private static string Section(int firstDay, int lastDay, string extra = "") =>
        $"/reports?preset=custom&startDate={Day(firstDay)}&endDate={Day(lastDay)}{extra}&section=cancellations";

    private static void SeedEndings(TenantDbContext db, DateTime now)
    {
        var count = 0;

        void Add(FoodPlatform platform, int daysAgo, OrderStatus status, OrderCancellationOrigin? origin = null, int? reason = null)
        {
            count++;
            var code = $"END-{count}";
            var at = DateOnly.FromDateTime(now.AddHours(3)).AddDays(-daysAgo).ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc).AddMinutes(count);
            db.Orders.Add(new Order
            {
                Id = Guid.NewGuid(),
                Platform = platform,
                ExternalOrderId = code,
                ExternalOrderCode = code,
                IdempotencyKey = $"{platform}:{code}",
                InternalStatus = status,
                PlatformStatus = status.ToString(),
                CustomerName = "Rapor Testi",
                CustomerPhone = "5551112233",
                CustomerAddress = "Test",
                TotalAmount = 10m,
                CreatedAtPlatform = at,
                ReceivedAt = at,
                CancelledAt = status == OrderStatus.Cancelled ? at.AddMinutes(4) : null,
                RawPayloadJson = "{}",
                CancellationOrigin = origin,
                CancellationReasonCode = reason,
                CancellationReasonPlatform = reason is null ? null : platform
            });
        }

        const FoodPlatform Trendyol = FoodPlatform.TrendyolYemek;
        foreach (var _ in Enumerable.Range(0, 3)) Add(Trendyol, MixedDay, OrderStatus.Cancelled, OrderCancellationOrigin.OperatorRejected, 621);
        foreach (var _ in Enumerable.Range(0, 2)) Add(Trendyol, MixedDay, OrderStatus.Cancelled, OrderCancellationOrigin.ProviderOrCustomer, 604);
        Add(Trendyol, MixedDay, OrderStatus.Cancelled, OrderCancellationOrigin.ProviderOrCustomer, 648);
        Add(Trendyol, MixedDay, OrderStatus.Cancelled, OrderCancellationOrigin.RestaurantOnPlatform, 627);
        Add(Trendyol, MixedDay, OrderStatus.Cancelled, OrderCancellationOrigin.RestaurantOnPlatform);
        Add(Trendyol, MixedDay, OrderStatus.Cancelled);
        Add(FoodPlatform.Yemeksepeti, MixedDay, OrderStatus.Cancelled, OrderCancellationOrigin.OperatorRejected);
        Add(FoodPlatform.Yemeksepeti, MixedDay, OrderStatus.Cancelled);
        Add(FoodPlatform.GetirYemek, MixedDay, OrderStatus.Cancelled);
        Add(Trendyol, MixedDay, OrderStatus.Delivered);

        foreach (var _ in Enumerable.Range(0, 2)) Add(Trendyol, UnknownDay, OrderStatus.Cancelled);
        Add(Trendyol, UnknownDay, OrderStatus.Cancelled, OrderCancellationOrigin.RestaurantOnPlatform);

        Add(Trendyol, NoCancellationDay, OrderStatus.Delivered);
        Add(FoodPlatform.Yemeksepeti, NoCancellationDay, OrderStatus.New);
    }

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, bool mobile, CancellationToken ct)
    {
        var browser = await _host.BrowserAsync(culture, width, 900, mobile, ct);
        if (browser is null)
            Assert.Skip("No Chromium browser is installed.");
        return browser;
    }

    private async Task OpenAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        Assert.True(await browser.WaitForAsync($"!!{Result} && document.getElementById('rptResult').getAttribute('aria-busy') !== 'true'", TimeSpan.FromSeconds(15), ct), $"{path} did not render. {_host.ServerErrors}");
    }

    private const string Snapshot = """
        (() => {
          const number = element => Number(element.textContent.replace(/[^\d]/g, ''));
          const first = (root, selector) => { const e = root.querySelector(selector); return e ? number(e.querySelector('bdi')) : null; };
          const ending = name => first(document, `[data-rpt-ending="${name}"]`);
          const groups = {};
          document.querySelectorAll('[data-rpt-reason-group]').forEach(group => {
            const reasons = {};
            group.querySelectorAll('[data-rpt-reason]').forEach(row => { reasons[row.dataset.rptReason] = number(row.querySelector('.rpt-figure bdi')); });
            groups[group.dataset.rptReasonGroup] = { reasons, missing: first(group, '[data-rpt-reason-missing]') };
          });
          return {
            total: Number(document.querySelector('.rpt-figure--xl bdi').textContent.replace(/[^\d]/g, '')),
            rejected: ending('known-rejected'), cancelled: ending('known-cancelled'), unknown: ending('unknown'),
            restaurant: first(document, '[data-rpt-unknown="restaurant"]'), noOrigin: first(document, '[data-rpt-unknown="none"]'),
            groups
          };
        })()
        """;

    [Fact]
    public async Task MixedCoverage_ShowsExactFigures_ThatAddUp_AndThePlatformFilterAppliesInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", 1280, false, ct);
        await OpenAsync(browser, Section(MixedDay, MixedDay), ct);

        var all = await browser.EvaluateAsync<JsonElement>(Snapshot, ct);
        Assert.Equal(12, all.GetProperty("total").GetInt32());
        Assert.Equal(4, all.GetProperty("rejected").GetInt32());
        Assert.Equal(3, all.GetProperty("cancelled").GetInt32());
        Assert.Equal(5, all.GetProperty("unknown").GetInt32());
        Assert.Equal(2, all.GetProperty("restaurant").GetInt32());
        Assert.Equal(3, all.GetProperty("noOrigin").GetInt32());
        Assert.Equal(all.GetProperty("total").GetInt32(), all.GetProperty("rejected").GetInt32() + all.GetProperty("cancelled").GetInt32() + all.GetProperty("unknown").GetInt32());

        var groups = all.GetProperty("groups");
        Assert.Equal(["TrendyolYemek", "Yemeksepeti", "GetirYemek"], groups.EnumerateObject().Select(g => g.Name));
        var trendyol = groups.GetProperty("TrendyolYemek");
        Assert.Equal(3, trendyol.GetProperty("reasons").GetProperty("621").GetInt32());
        Assert.Equal(2, trendyol.GetProperty("reasons").GetProperty("604").GetInt32());
        Assert.Equal(1, trendyol.GetProperty("reasons").GetProperty("648").GetInt32());
        Assert.Equal(1, trendyol.GetProperty("reasons").GetProperty("627").GetInt32());
        Assert.Equal(2, trendyol.GetProperty("missing").GetInt32());
        Assert.Equal(2, groups.GetProperty("Yemeksepeti").GetProperty("missing").GetInt32());
        Assert.Equal(1, groups.GetProperty("GetirYemek").GetProperty("missing").GetInt32());
        Assert.Contains("7 / 12", await browser.EvaluateAsync<string>("document.querySelector('[data-rpt-reason-coverage]').textContent", ct), StringComparison.Ordinal);
        Assert.True(await browser.EvaluateAsync<bool>("!!document.querySelector('[data-rpt-missing-data-note]')", ct));

        await browser.EvaluateAsync<bool>("(window.__samePage = true, (() => { const s = document.getElementById('rptPlatform'); s.value = 'Yemeksepeti'; s.dispatchEvent(new Event('change', { bubbles: true })); })(), true)", ct);
        Assert.True(await browser.WaitForAsync("location.search.includes('platform=Yemeksepeti') && !!document.querySelector('[data-rpt-reason-group=Yemeksepeti]') && !document.querySelector('[data-rpt-reason-group=TrendyolYemek]') && document.getElementById('rptResult').getAttribute('aria-busy') !== 'true'", TimeSpan.FromSeconds(15), ct), "The platform filter did not apply.");
        Assert.True(await browser.EvaluateAsync<bool>("window.__samePage === true", ct), "The filter reloaded the page.");

        var one = await browser.EvaluateAsync<JsonElement>(Snapshot, ct);
        Assert.Equal(2, one.GetProperty("total").GetInt32());
        Assert.Equal(1, one.GetProperty("rejected").GetInt32());
        Assert.Equal(0, one.GetProperty("cancelled").GetInt32());
        Assert.Equal(1, one.GetProperty("unknown").GetInt32());
        Assert.Equal(["Yemeksepeti"], one.GetProperty("groups").EnumerateObject().Select(g => g.Name));
        Assert.Equal(2, one.GetProperty("groups").GetProperty("Yemeksepeti").GetProperty("missing").GetInt32());
        Assert.True(await browser.EvaluateAsync<bool>("!!document.querySelector('[data-rpt-no-reason-list]') && !document.querySelector('[data-rpt-ending-platforms]')", ct));
    }

    [Fact]
    public async Task AllReasonsUnknown_SaysSo_WithoutAnyReasonRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", 1280, false, ct);
        await OpenAsync(browser, Section(UnknownDay, UnknownDay), ct);

        var report = await browser.EvaluateAsync<JsonElement>(Snapshot, ct);
        Assert.Equal(3, report.GetProperty("total").GetInt32());
        Assert.Equal(3, report.GetProperty("unknown").GetInt32());
        Assert.Equal(1, report.GetProperty("restaurant").GetInt32());
        Assert.Equal(3, report.GetProperty("groups").GetProperty("TrendyolYemek").GetProperty("missing").GetInt32());
        Assert.Empty(report.GetProperty("groups").GetProperty("TrendyolYemek").GetProperty("reasons").EnumerateObject());
        Assert.True(await browser.EvaluateAsync<bool>("!!document.querySelector('[data-rpt-no-reasons]') && !!document.querySelector('[data-rpt-missing-data-note]')", ct));
        Assert.Contains("0 / 3", await browser.EvaluateAsync<string>("document.querySelector('[data-rpt-reason-coverage]').textContent", ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US", "ltr", 1280, false)]
    [InlineData("ar-SA", "rtl", 390, true)]
    public async Task NoCancellations_IsItsOwnState(string culture, string direction, int width, bool mobile)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, mobile, ct);
        await OpenAsync(browser, Section(NoCancellationDay, NoCancellationDay), ct);

        Assert.Equal(direction, await browser.EvaluateAsync<string>("document.documentElement.dir", ct));
        Assert.True(await browser.EvaluateAsync<bool>("!!document.querySelector('[data-rpt-no-cancellations] .rpt-state__title') && !document.querySelector('[data-rpt-breakdown=ending]') && !document.querySelector('[data-rpt-breakdown=reasons]') && !document.querySelector('[data-rpt-missing-data-note]')", ct));
        Assert.False(await browser.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1", ct));
    }

    public static TheoryData<string, string, int, bool, string, string> Layouts => new()
    {
        { "tr-TR", "ltr", 1280, false, "Tedarik problemi", "Nasıl sonuçlandı" },
        { "tr-TR", "ltr", 390, true, "Kurye kaynaklı - Site girişi kimlik sorunu", "Bilinen ret" },
        { "en-US", "ltr", 1280, false, "Customer asked to cancel", "How these orders ended" },
        { "en-US", "ltr", 390, true, "Courier-related: ID problem at the site entrance", "Known rejections" },
        { "ar-SA", "rtl", 1280, false, "طلب العميل الإلغاء", "كيف انتهت هذه الطلبات" },
        { "ar-SA", "rtl", 390, true, "بسبب المندوب - مشكلة هوية عند مدخل الموقع", "رفض معروف" },
        { "ru-RU", "ltr", 1280, false, "Клиент запросил отмену", "Как завершились эти заказы" },
        { "ru-RU", "ltr", 390, true, "По вине курьера: проблема с удостоверением на входе", "Подтверждённые отказы" }
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task EveryCulture_TranslatesTheSection_AndLongLabelsStayInTheirRows(string culture, string direction, int width, bool mobile, string reason, string heading)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, mobile, ct);
        await OpenAsync(browser, Section(MixedDay, MixedDay - 1), ct);

        Assert.Equal(direction, await browser.EvaluateAsync<string>("document.documentElement.dir", ct));
        var text = await browser.EvaluateAsync<string>("document.querySelector('#rptResult').innerText", ct);
        Assert.Contains(reason, text, StringComparison.Ordinal);
        Assert.Contains(heading, text, StringComparison.Ordinal);

        // No technical name reaches the page, and nothing leaves its row or the screen.
        foreach (var technical in new[] { "OperatorRejected", "ProviderOrCustomer", "RestaurantOnPlatform", "Reports.", "Orders.RejectReason" })
            Assert.DoesNotContain(technical, text, StringComparison.Ordinal);
        Assert.False(await browser.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1", ct), "The page scrolls sideways.");
        var clipped = await browser.EvaluateAsync<int>("""
            [...document.querySelectorAll('.rpt-reason, .rpt-ending, .rpt-reason-group__head')].filter(row => {
              const box = row.getBoundingClientRect();
              return [...row.querySelectorAll('*')].some(child => {
                const r = child.getBoundingClientRect();
                return r.width > 0 && (r.left < box.left - 1 || r.right > box.right + 1);
              });
            }).length
            """, ct);
        Assert.Equal(0, clipped);

        // A reason's bar takes a real share of its row at every width, so the bars can be compared.
        Assert.True(await browser.EvaluateAsync<bool>("""
            [...document.querySelectorAll('.rpt-reason')].every(row =>
              row.querySelector('.rpt-share-row__track').getBoundingClientRect().width >= row.getBoundingClientRect().width * 0.4)
            """, ct), "A reason's bar is squeezed.");
    }

    [Theory]
    [InlineData("en-US", 1280, false)]
    [InlineData("ar-SA", 390, true)]
    public async Task TheTables_AreReachedByKeyboard_AndTheDailyViewStillToggles(string culture, int width, bool mobile)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, mobile, ct);
        await OpenAsync(browser, Section(MixedDay, MixedDay - 1), ct);

        // From just before the by-platform table, Tab lands on its scroll region with a visible focus ring.
        await browser.EvaluateAsync<bool>("""
            (() => {
              const region = document.querySelector('[data-rpt-breakdown=ending] [role=region]');
              const before = document.createElement('button');
              before.id = '__before';
              region.before(before);
              before.focus();
              return true;
            })()
            """, ct);
        await browser.PressKeyAsync("Tab", shift: false, ct);
        Assert.True(await browser.WaitForAsync("document.activeElement.matches('[data-rpt-breakdown=ending] [role=region]')", TimeSpan.FromSeconds(5), ct), "Tab did not reach the by-platform table.");
        Assert.True(await browser.EvaluateAsync<bool>("document.activeElement.matches(':focus-visible') && getComputedStyle(document.activeElement).outlineStyle === 'solid'", ct), "The focused table has no visible ring.");
        Assert.False(string.IsNullOrWhiteSpace(await browser.EvaluateAsync<string>("document.activeElement.getAttribute('aria-label')", ct)));

        await browser.EvaluateAsync<bool>("(document.getElementById('__before').remove(), document.querySelector('[data-rpt-panel=cancel-daily] [data-rpt-view=table]').click(), true)", ct);
        Assert.True(await browser.EvaluateAsync<bool>("""
            (() => {
              const panel = document.querySelector('[data-rpt-panel=cancel-daily]');
              return !panel.querySelector('[data-rpt-panel-table]').hidden
                && panel.querySelectorAll('tbody tr').length === 2
                && panel.querySelectorAll('thead th').length === 8
                && panel.querySelector('[role=region]').tabIndex === 0;
            })()
            """, ct));
    }

    [Fact]
    public async Task ADayOfTheChart_ReadsTheSplit_ByKeyboard()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", 1280, false, ct);
        await OpenAsync(browser, Section(MixedDay, MixedDay - 1), ct);

        var label = await browser.EvaluateAsync<string>("document.querySelector('[data-rpt-panel=cancel-daily] [data-rpt-col][aria-label*=\"Known rejections 4\"]')?.getAttribute('aria-label') ?? ''", ct);

        Assert.Contains("Known rejections 4", label, StringComparison.Ordinal);
        Assert.Contains("Known cancellations 3", label, StringComparison.Ordinal);
        Assert.Contains("Rejection or cancellation not known 5", label, StringComparison.Ordinal);
    }
}
