using System.Globalization;
using System.Net;
using System.Resources;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Billing;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.UnitTests.Billing;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// Correcting and superseding plan prices on the Central Admin page over real HTTP (GitHub #64): only a signed-in
/// Central Admin with the antiforgery token can correct; each row offers Correct or, once a subscription uses it,
/// Supersede with the reason it cannot change; a correction is validated like a new price and recorded with the admin;
/// a subscription that starts using the price after the page was loaded makes the correction refuse and the page offer
/// supersede; and a superseding price is a new row that leaves the referenced one as it was.
/// </summary>
public sealed class AdminBillingPlanPriceCorrectionWebTests : IAsyncLifetime
{
    private const string Page = "/admin/billing/plan-prices";
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly ResourceManager Resources = new("Wasla.Web.Resources.SharedResource", typeof(Wasla.Web.SharedResource).Assembly);

    private readonly CentralTestDatabase _central = new();
    private readonly RecordingTenantDbFactory _tenants = new();
    private AdminWebHost _host = null!;

    private static DateOnly TodayInTurkey => BillingPlanPriceInput.TurkeyDate(DateTime.UtcNow);

    public async ValueTask InitializeAsync() => _host = await AdminWebHost.StartAsync(_central, _tenants);

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _tenants.Dispose();
        _central.Dispose();
    }

    [Fact]
    public async Task Anonymous_AndATenantSession_CannotOpenOrPostACorrectionOrASupersede()
    {
        var price = AddPrice(TodayInTurkey.AddDays(3));
        using var anonymous = _host.NewBrowser();
        using var tenant = _host.NewBrowser();
        await tenant.SignInTenantUserAsync(Guid.NewGuid());

        foreach (var browser in new[] { anonymous, tenant })
        {
            var correct = await browser.GetAsync($"{Page}/{price.Id}/correct");
            var supersede = await browser.GetAsync($"{Page}/{price.Id}/supersede");
            var post = await browser.PostFormAsync($"{Page}/{price.Id}/correct", CorrectionForm(), antiforgeryToken: null);

            Assert.All(new[] { correct, supersede }, r =>
            {
                Assert.Equal(HttpStatusCode.Redirect, r.Status);
                Assert.Contains("/admin/login", r.Location!.ToString(), StringComparison.Ordinal);
            });
            Assert.NotEqual(HttpStatusCode.OK, post.Status);
        }

        AssertUnchanged(price);
    }

    [Fact]
    public async Task ACentralAdmin_WithoutTheAntiforgeryToken_CannotCorrect()
    {
        var price = AddPrice(TodayInTurkey.AddDays(3));
        using var browser = await _host.SignedInAdminAsync("en-US");

        var post = await browser.PostFormAsync($"{Page}/{price.Id}/correct", CorrectionForm(), antiforgeryToken: null);

        Assert.Equal(HttpStatusCode.BadRequest, post.Status);
        AssertUnchanged(price);
    }

    [Fact]
    public async Task ACentralAdmin_CorrectsAnUnreferencedPrice_AndTheChangeIsRecordedWithTheAdminAndBothValues()
    {
        var price = AddPrice(TodayInTurkey, kurus: 35900, period: "Yearly");
        using var browser = await _host.SignedInAdminAsync("en-US");

        var list = WebUtility.HtmlDecode((await browser.GetAsync(Page)).Body);
        var row = Row(list, $"data-plan-price=\"{price.Id}\"");
        Assert.Contains($"href=\"{Page}/{price.Id}/correct\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("/supersede", row, StringComparison.Ordinal);

        var form = await browser.GetAsync($"{Page}/{price.Id}/correct");
        Assert.Equal(HttpStatusCode.OK, form.Status);
        Assert.Contains("name=\"AmountKurus\" type=\"text\"", form.Body, StringComparison.Ordinal);
        Assert.Contains("value=\"35900\"", form.Body, StringComparison.Ordinal);
        Assert.Contains("name=\"Revision\" value=\"1\"", form.Body, StringComparison.Ordinal);

        var post = await browser.PostFormAsync(
            $"{Page}/{price.Id}/correct",
            CorrectionForm(kurus: "499900", vat: "false", date: TodayInTurkey.AddDays(1)),
            AdminBrowser.AntiforgeryTokenIn(form.Body));

        Assert.Equal(HttpStatusCode.Redirect, post.Status);
        Assert.Equal(Page, post.Location!.ToString());
        using (var db = _central.CreateContext())
        {
            var stored = db.BillingPlanPrices.AsNoTracking().Single(p => p.Id == price.Id);
            Assert.Equal((4999.00m, false, BillingPlanPriceInput.EffectiveFromUtc(TodayInTurkey.AddDays(1)), 2), (stored.Amount, stored.VatIncluded, stored.EffectiveFromUtc, stored.Revision));
            var audit = db.BillingPlanPriceCorrections.AsNoTracking().Single();
            Assert.Equal((price.Id, AdminId()), (audit.PlanPriceId, audit.CorrectedByCentralAdminUserId));
            Assert.Equal((359.00m, true, 4999.00m, false), (audit.PreviousAmount, audit.PreviousVatIncluded, audit.NewAmount, audit.NewVatIncluded));
        }

        Assert.Contains(Text("Admin.PlanPrices.Corrected"), WebUtility.HtmlDecode((await browser.GetAsync(Page)).Body), StringComparison.Ordinal);
        var history = WebUtility.HtmlDecode((await browser.GetAsync($"{Page}/{price.Id}/correct")).Body);
        Assert.Contains(Text("Admin.PlanPrices.CorrectionHistory"), history, StringComparison.Ordinal);
        Assert.Contains("Ops", Row(history, "data-correction-by="), StringComparison.Ordinal);
        Assert.Contains("359.00 TRY", history, StringComparison.Ordinal);
        Assert.Contains("4,999.00 TRY", history, StringComparison.Ordinal);
    }

    public static TheoryData<string, string?, string> InvalidCorrections() => new()
    {
        { "AmountKurus", "", "Admin.PlanPrices.Error.AmountRequired" },
        { "AmountKurus", "12,50", "Admin.PlanPrices.Error.AmountInvalid" },
        { "AmountKurus", "0", "Admin.PlanPrices.Error.AmountRange" },
        { "VatIncluded", null, "Admin.PlanPrices.Error.VatRequired" },
        { "EffectiveDate", "2026-02-30", "Admin.PlanPrices.Error.DateInvalid" },
        { "EffectiveDate", "yesterday", "Admin.PlanPrices.Error.DatePast" }
    };

    [Theory]
    [MemberData(nameof(InvalidCorrections))]
    public async Task AnInvalidCorrection_IsRefusedWithItsOwnMessage_AndNothingChanges(string field, string? value, string messageKey)
    {
        var price = AddPrice(TodayInTurkey.AddDays(3));
        using var browser = await _host.SignedInAdminAsync("en-US");
        var form = CorrectionForm();
        if (value is null)
            form.Remove(field);
        else
            form[field] = value == "yesterday" ? Iso(TodayInTurkey.AddDays(-1)) : value;

        var page = await browser.GetAsync($"{Page}/{price.Id}/correct");
        var response = await browser.PostFormAsync($"{Page}/{price.Id}/correct", form, AdminBrowser.AntiforgeryTokenIn(page.Body));

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var body = WebUtility.HtmlDecode(response.Body);
        Assert.Contains(Text("Admin.PlanPrices.Error.Summary"), body, StringComparison.Ordinal);
        Assert.Contains($"id=\"{field}-error\"", body, StringComparison.Ordinal);
        Assert.Contains(Text(messageKey), body, StringComparison.Ordinal);
        AssertUnchanged(price);
    }

    [Fact]
    public async Task AReferencedPrice_ShowsWhyItCannotChange_OffersSupersede_AndRefusesACorrection()
    {
        var price = AddPrice(TodayInTurkey.AddDays(-2));
        AddSubscription(price);
        AddSubscription(price);
        using var browser = await _host.SignedInAdminAsync("en-US");

        var row = Row(WebUtility.HtmlDecode((await browser.GetAsync(Page)).Body), $"data-plan-price=\"{price.Id}\"");
        Assert.Contains(string.Format(English, Text("Admin.PlanPrices.ReferencedCount"), "2"), row, StringComparison.Ordinal);
        Assert.Contains($"href=\"{Page}/{price.Id}/supersede\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("/correct\"", row, StringComparison.Ordinal);

        var page = await browser.GetAsync($"{Page}/{price.Id}/correct");
        var body = WebUtility.HtmlDecode(page.Body);
        Assert.Contains(string.Format(English, Text("Admin.PlanPrices.ReferencedExplanation"), "2"), body, StringComparison.Ordinal);
        Assert.Contains($"href=\"{Page}/{price.Id}/supersede\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"AmountKurus\"", body, StringComparison.Ordinal);

        // A crafted post is refused on the server, and the page it leads to offers supersede.
        var post = await browser.PostFormAsync($"{Page}/{price.Id}/correct", CorrectionForm(), AdminBrowser.AntiforgeryTokenIn(page.Body));

        Assert.Equal(HttpStatusCode.Redirect, post.Status);
        Assert.Equal($"{Page}/{price.Id}/correct", post.Location!.ToString());
        AssertUnchanged(price);
        var refused = WebUtility.HtmlDecode((await browser.GetAsync(post.Location.ToString())).Body);
        Assert.Contains(string.Format(English, Text("Admin.PlanPrices.CorrectionRefusedReferenced"), "2"), refused, StringComparison.Ordinal);
        Assert.Contains($"href=\"{Page}/{price.Id}/supersede\"", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASubscriptionThatStartsUsingThePriceAfterThePageWasLoaded_MakesTheCorrectionRefuseAndOfferSupersede()
    {
        var price = AddPrice(TodayInTurkey);
        using var browser = await _host.SignedInAdminAsync("en-US");
        var page = await browser.GetAsync($"{Page}/{price.Id}/correct");
        Assert.Contains("name=\"AmountKurus\"", page.Body, StringComparison.Ordinal);

        AddSubscription(price);
        var post = await browser.PostFormAsync($"{Page}/{price.Id}/correct", CorrectionForm(date: TodayInTurkey), AdminBrowser.AntiforgeryTokenIn(page.Body));

        Assert.Equal(HttpStatusCode.Redirect, post.Status);
        AssertUnchanged(price);
        var refused = WebUtility.HtmlDecode((await browser.GetAsync(post.Location!.ToString())).Body);
        Assert.Contains(string.Format(English, Text("Admin.PlanPrices.CorrectionRefusedReferenced"), "1"), refused, StringComparison.Ordinal);
        Assert.Contains($"href=\"{Page}/{price.Id}/supersede\"", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFormLoadedBeforeAnotherCorrection_IsRefusedAsStale_AndShowsTheCurrentValues()
    {
        var price = AddPrice(TodayInTurkey.AddDays(3));
        using var first = await _host.SignedInAdminAsync("en-US");
        using var second = await _host.SignedInAdminAsync("en-US");
        var firstPage = await first.GetAsync($"{Page}/{price.Id}/correct");
        var secondPage = await second.GetAsync($"{Page}/{price.Id}/correct");

        Assert.Equal(HttpStatusCode.Redirect, (await first.PostFormAsync($"{Page}/{price.Id}/correct", CorrectionForm(kurus: "20000"), AdminBrowser.AntiforgeryTokenIn(firstPage.Body))).Status);
        var stale = await second.PostFormAsync($"{Page}/{price.Id}/correct", CorrectionForm(kurus: "30000"), AdminBrowser.AntiforgeryTokenIn(secondPage.Body));

        Assert.Equal($"{Page}/{price.Id}/correct", stale.Location!.ToString());
        var reloaded = WebUtility.HtmlDecode((await second.GetAsync(stale.Location.ToString())).Body);
        Assert.Contains(Text("Admin.PlanPrices.CorrectionStale"), reloaded, StringComparison.Ordinal);
        Assert.Contains("value=\"20000\"", reloaded, StringComparison.Ordinal);
        using var db = _central.CreateContext();
        Assert.Equal(200m, db.BillingPlanPrices.AsNoTracking().Single(p => p.Id == price.Id).Amount);
    }

    [Fact]
    public async Task Supersede_PrefillsANewPriceFromTodayOn_AndSavingItLeavesTheReferencedPriceAsItWas()
    {
        var price = AddPrice(TodayInTurkey.AddDays(-2), kurus: 49990);
        AddSubscription(price);
        using var browser = await _host.SignedInAdminAsync("en-US");

        var page = await browser.GetAsync($"{Page}/{price.Id}/supersede");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        var body = WebUtility.HtmlDecode(page.Body);
        Assert.Contains($"data-supersedes=\"{price.Id}\"", body, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Starter\" selected=\"selected\">", body, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Monthly\" selected=\"selected\">", body, StringComparison.Ordinal);
        Assert.Contains("value=\"49990\"", body, StringComparison.Ordinal);
        Assert.Contains($"name=\"EffectiveDate\" type=\"date\" min=\"{Iso(TodayInTurkey)}\" dir=\"ltr\"", body, StringComparison.Ordinal);
        Assert.Contains($"value=\"{Iso(TodayInTurkey)}\"", body, StringComparison.Ordinal);

        var post = await browser.PostFormAsync(Page, new Dictionary<string, string>
        {
            ["PlanCode"] = "Starter",
            ["BillingPeriod"] = "Monthly",
            ["AmountKurus"] = "59990",
            ["VatIncluded"] = "true",
            ["EffectiveDate"] = Iso(TodayInTurkey)
        }, AdminBrowser.AntiforgeryTokenIn(page.Body));

        Assert.Equal(HttpStatusCode.Redirect, post.Status);
        AssertUnchanged(price);
        using var db = _central.CreateContext();
        var replacement = db.BillingPlanPrices.AsNoTracking().Single(p => p.Id != price.Id);
        Assert.Equal((599.90m, BillingPlanPriceInput.EffectiveFromUtc(TodayInTurkey)), (replacement.Amount, replacement.EffectiveFromUtc));
        var list = WebUtility.HtmlDecode((await browser.GetAsync(Page)).Body);
        Assert.Contains("599.90 TRY", Row(list, "data-plan=\"Starter\" data-period=\"Monthly\""), StringComparison.Ordinal);
        // The replaced price is history now: it keeps its reference count and offers nothing.
        var replaced = Row(list, $"data-plan-price=\"{price.Id}\"");
        Assert.Contains(Text("Admin.PlanPrices.Status.Superseded"), replaced, StringComparison.Ordinal);
        Assert.DoesNotContain("/supersede\"", replaced, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownPrice_LeadsBackToTheListWithAMessage()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");

        var correct = await browser.GetAsync($"{Page}/{Guid.NewGuid()}/correct");
        var supersede = await browser.GetAsync($"{Page}/{Guid.NewGuid()}/supersede");

        Assert.All(new[] { correct, supersede }, r => Assert.Equal(Page, r.Location!.ToString()));
        Assert.Contains(Text("Admin.PlanPrices.NotFound"), WebUtility.HtmlDecode((await browser.GetAsync(Page)).Body), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tr-TR", "Düzelt", "Yeni fiyatla değiştir")]
    [InlineData("ar-SA", "تصحيح", "استبدال بسعر جديد")]
    [InlineData("ru-RU", "Исправить", "Заменить новой ценой")]
    public async Task TheRowActions_AreLocalized_AndTheArabicPageIsRightToLeft(string culture, string correct, string supersede)
    {
        AddPrice(TodayInTurkey.AddDays(3));
        AddSubscription(AddPrice(TodayInTurkey.AddDays(-1), period: "Yearly"));
        using var browser = await _host.SignedInAdminAsync(culture);

        var body = WebUtility.HtmlDecode((await browser.GetAsync(Page)).Body);

        Assert.Contains(correct, body, StringComparison.Ordinal);
        Assert.Contains(supersede, body, StringComparison.Ordinal);
        if (culture == "ar-SA")
            Assert.Contains("dir=\"rtl\"", body, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> CorrectionForm(
        string kurus = "12345",
        string vat = "true",
        DateOnly? date = null,
        string revision = "1") => new()
    {
        ["Revision"] = revision,
        ["AmountKurus"] = kurus,
        ["VatIncluded"] = vat,
        ["EffectiveDate"] = Iso(date ?? TodayInTurkey.AddDays(3))
    };

    private BillingPlanPrice AddPrice(DateOnly date, long kurus = 5000, string period = "Monthly")
    {
        using var db = _central.CreateContext();
        var price = BillingPlanPrice.Create(
            "Starter",
            period,
            BillingPrice.FromMinorUnits(kurus, "TRY"),
            vatIncluded: true,
            BillingPlanPriceInput.EffectiveFromUtc(date),
            Guid.NewGuid(),
            DateTime.UtcNow);
        db.BillingPlanPrices.Add(price);
        db.SaveChanges();
        return price;
    }

    /// <summary>A checkout started from <paramref name="price"/>: the subscription now references it.</summary>
    private void AddSubscription(BillingPlanPrice price)
    {
        using var db = _central.CreateContext();
        var registration = BillingTestData.Registration(planCode: price.PlanCode);
        db.PendingRegistrations.Add(registration);
        db.BillingSubscriptions.Add(BillingSubscription.StartPending(
            registration.Id,
            BillingTestData.Sandbox.Provider,
            BillingTestData.Sandbox.Environment,
            price,
            DateTime.UtcNow));
        db.SaveChanges();
    }

    private void AssertUnchanged(BillingPlanPrice price)
    {
        using var db = _central.CreateContext();
        var stored = db.BillingPlanPrices.AsNoTracking().Single(p => p.Id == price.Id);
        Assert.Equal((price.Amount, price.VatIncluded, price.EffectiveFromUtc, 1), (stored.Amount, stored.VatIncluded, stored.EffectiveFromUtc, stored.Revision));
        Assert.False(db.BillingPlanPriceCorrections.Any(c => c.PlanPriceId == price.Id));
    }

    private Guid AdminId()
    {
        using var db = _central.CreateContext();
        return db.CentralAdminUsers.Single(a => a.Email == AdminWebHost.AdminEmail).Id;
    }

    /// <summary>The table row that carries <paramref name="marker"/>.</summary>
    private static string Row(string body, string marker)
    {
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No row with {marker}");
        return body[start..body.IndexOf("</tr>", start, StringComparison.Ordinal)];
    }

    private static string Text(string key) => Resources.GetString(key, English) ?? throw new InvalidOperationException(key);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
