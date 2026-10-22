using System.Globalization;
using System.Net;
using System.Resources;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Billing;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// The Central Admin plan prices page over real HTTP (WAS-147): only a signed-in Central Admin can see or change
/// prices, every input is validated with its own localized message, a price takes effect at 00:00 Türkiye time on its
/// date, prices are recorded with the admin who set them, nothing is seeded, and only a price that has not taken effect
/// can be withdrawn.
/// </summary>
public sealed class AdminBillingPlanPricesWebTests : IAsyncLifetime
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
    public async Task Anonymous_IsSentToSignIn_AndCannotSchedule()
    {
        using var browser = _host.NewBrowser();

        var page = await browser.GetAsync(Page);
        var post = await browser.PostFormAsync(Page, ValidForm(), antiforgeryToken: null);

        Assert.Equal(HttpStatusCode.Redirect, page.Status);
        Assert.Contains("/admin/login", page.Location!.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.OK, post.Status);
        Assert.Empty(StoredPrices());
    }

    [Fact]
    public async Task ATenantSession_EvenWithACentralAdminRoleClaim_IsNotACentralAdmin()
    {
        using var browser = _host.NewBrowser();
        await browser.SignInTenantUserAsync(Guid.NewGuid());

        var page = await browser.GetAsync(Page);
        var post = await browser.PostFormAsync(Page, ValidForm(), antiforgeryToken: null);

        Assert.Equal(HttpStatusCode.Redirect, page.Status);
        Assert.Contains("/admin/login", page.Location!.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.OK, post.Status);
        Assert.Empty(StoredPrices());
    }

    [Fact]
    public async Task ACentralAdmin_WithoutTheAntiforgeryToken_CannotScheduleOrWithdraw()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var scheduled = AddPriceDirectly(TodayInTurkey.AddDays(3));

        var post = await browser.PostFormAsync(Page, ValidForm(), antiforgeryToken: null);
        var withdraw = await browser.PostAsync($"{Page}/{scheduled.Id}/withdraw", antiforgeryToken: null);

        Assert.Equal(HttpStatusCode.BadRequest, post.Status);
        Assert.Equal(HttpStatusCode.BadRequest, withdraw.Status);
        Assert.Equal([scheduled.Id], StoredPrices().Select(p => p.Id));
    }

    [Fact]
    public async Task NothingIsSeeded_AndEveryPlanAndPeriodThatCanBeBoughtOnline_ShowsCheckoutBlocked()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");

        var page = await browser.GetAsync(Page);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Empty(StoredPrices());
        var body = WebUtility.HtmlDecode(page.Body);
        foreach (var plan in new[] { "Starter", "Pro", "ProPlus" })
        foreach (var period in new[] { "Monthly", "Yearly" })
        {
            var row = Row(body, $"data-plan=\"{plan}\" data-period=\"{period}\"");
            Assert.Contains(Text("Admin.PlanPrices.NoPrice"), row, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("data-plan=\"Enterprise\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"Enterprise\"", body, StringComparison.Ordinal);
        Assert.Contains(Text("Admin.PlanPrices.HistoryEmpty"), body, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/billing/plan-prices\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACentralAdmin_SchedulesAPriceInKurus_WithItsVatChoice_FromMidnightTurkeyTime_AndIsRecordedAsItsAuthor()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var date = TodayInTurkey.AddDays(10);

        var post = await PostAsync(browser, ValidForm(plan: "Pro", period: "Yearly", kurus: "2999000", vat: "false", date: date));

        Assert.Equal(HttpStatusCode.Redirect, post.Status);
        Assert.Equal(Page, post.Location!.ToString());
        var stored = Assert.Single(StoredPrices());
        Assert.Equal(("Pro", "Yearly"), (stored.PlanCode, stored.BillingPeriod));
        Assert.Equal(new BillingPrice(29990.00m, "TRY"), stored.Price);
        Assert.False(stored.VatIncluded);
        Assert.Equal(date.ToDateTime(TimeOnly.MinValue).AddHours(-3), stored.EffectiveFromUtc);
        Assert.Equal(AdminId(), stored.CreatedByCentralAdminUserId);

        var body = WebUtility.HtmlDecode((await browser.GetAsync(Page)).Body);
        Assert.Contains(Text("Admin.PlanPrices.Saved"), body, StringComparison.Ordinal);
        Assert.Contains("29,990.00 TRY", body, StringComparison.Ordinal);
        Assert.Contains("2,999,000 kuruş", body, StringComparison.Ordinal);
        Assert.Contains(Text("Admin.PlanPrices.Status.Scheduled"), body, StringComparison.Ordinal);
        Assert.Contains(Text("Billing.VatExcluded"), body, StringComparison.Ordinal);
        // Scheduled, not in effect: checkout for Pro yearly stays blocked until that date.
        Assert.Contains(Text("Admin.PlanPrices.NoPrice"), Row(body, "data-plan=\"Pro\" data-period=\"Yearly\""), StringComparison.Ordinal);
    }

    public static TheoryData<string, string?, string> InvalidInputs() => new()
    {
        { "PlanCode", "", "Admin.PlanPrices.Error.Plan" },
        { "PlanCode", "Enterprise", "Admin.PlanPrices.Error.Plan" },
        { "PlanCode", "Gold", "Admin.PlanPrices.Error.Plan" },
        { "PlanCode", "pro", "Admin.PlanPrices.Error.Plan" },
        { "BillingPeriod", "", "Admin.PlanPrices.Error.Period" },
        { "BillingPeriod", "Weekly", "Admin.PlanPrices.Error.Period" },
        { "AmountKurus", "", "Admin.PlanPrices.Error.AmountRequired" },
        { "AmountKurus", "12.5", "Admin.PlanPrices.Error.AmountInvalid" },
        { "AmountKurus", "12,5", "Admin.PlanPrices.Error.AmountInvalid" },
        { "AmountKurus", "-500", "Admin.PlanPrices.Error.AmountInvalid" },
        { "AmountKurus", "1e3", "Admin.PlanPrices.Error.AmountInvalid" },
        { "AmountKurus", "١٢٣", "Admin.PlanPrices.Error.AmountInvalid" },
        { "AmountKurus", "0", "Admin.PlanPrices.Error.AmountRange" },
        { "AmountKurus", "1000000001", "Admin.PlanPrices.Error.AmountRange" },
        { "AmountKurus", "99999999999999999999999", "Admin.PlanPrices.Error.AmountRange" },
        { "VatIncluded", null, "Admin.PlanPrices.Error.VatRequired" },
        { "VatIncluded", "maybe", "Admin.PlanPrices.Error.VatRequired" },
        { "EffectiveDate", "", "Admin.PlanPrices.Error.DateRequired" },
        { "EffectiveDate", "2026-13-01", "Admin.PlanPrices.Error.DateInvalid" },
        { "EffectiveDate", "01.11.2030", "Admin.PlanPrices.Error.DateInvalid" },
        { "EffectiveDate", "yesterday", "Admin.PlanPrices.Error.DatePast" }
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task AnInvalidInput_IsRefusedWithItsOwnMessage_AndNothingIsStored(string field, string? value, string messageKey)
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var form = new Dictionary<string, string>(ValidForm());
        if (value is null)
            form.Remove(field);
        else
            form[field] = value == "yesterday" ? Iso(TodayInTurkey.AddDays(-1)) : value;

        var response = await PostAsync(browser, form);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var body = WebUtility.HtmlDecode(response.Body);
        Assert.Contains(Text("Admin.PlanPrices.Error.Summary"), body, StringComparison.Ordinal);
        Assert.Contains($"id=\"{field}-error\"", body, StringComparison.Ordinal);
        Assert.Contains(Text(messageKey), body, StringComparison.Ordinal);
        Assert.Empty(StoredPrices());
    }

    [Fact]
    public async Task ASecondPriceForThePlanAndPeriodOnTheSameDate_IsRefused_ButAnotherDateOrPeriodIsNot()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var date = TodayInTurkey.AddDays(4);

        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(browser, ValidForm(kurus: "10000", date: date))).Status);
        var duplicate = await PostAsync(browser, ValidForm(kurus: "20000", date: date));
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(browser, ValidForm(kurus: "20000", date: date.AddDays(1)))).Status);
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(browser, ValidForm(period: "Yearly", kurus: "20000", date: date))).Status);

        Assert.Equal(HttpStatusCode.OK, duplicate.Status);
        Assert.Contains(Text("Admin.PlanPrices.Error.Duplicate"), WebUtility.HtmlDecode(duplicate.Body), StringComparison.Ordinal);
        var stored = StoredPrices();
        Assert.Equal(3, stored.Count);
        Assert.Equal(100m, stored.Single(p => p.BillingPeriod == "Monthly" && p.EffectiveFromUtc == BillingPlanPriceInput.EffectiveFromUtc(date)).Amount);
    }

    [Fact]
    public async Task OnlyAPriceThatHasNotTakenEffect_CanBeWithdrawn()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var inEffect = AddPriceDirectly(TodayInTurkey.AddDays(-2));
        var scheduled = AddPriceDirectly(TodayInTurkey.AddDays(6));

        var page = await browser.GetAsync(Page);
        var token = AdminBrowser.AntiforgeryTokenIn(page.Body);
        Assert.Contains($"{Page}/{scheduled.Id}/withdraw", page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain($"{Page}/{inEffect.Id}/withdraw", page.Body, StringComparison.Ordinal);

        var refused = await browser.PostAsync($"{Page}/{inEffect.Id}/withdraw", token);
        var unknown = await browser.PostAsync($"{Page}/{Guid.NewGuid()}/withdraw", token);
        var withdrawn = await browser.PostAsync($"{Page}/{scheduled.Id}/withdraw", token);

        Assert.All(new[] { refused, unknown, withdrawn }, r => Assert.Equal(HttpStatusCode.Redirect, r.Status));
        Assert.Equal([inEffect.Id], StoredPrices().Select(p => p.Id));
        Assert.Contains(Text("Admin.PlanPrices.Withdrawn"), WebUtility.HtmlDecode((await browser.GetAsync(Page)).Body), StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ValidForm(
        string plan = "Starter",
        string period = "Monthly",
        string kurus = "12345",
        string vat = "true",
        DateOnly? date = null) => new()
    {
        ["PlanCode"] = plan,
        ["BillingPeriod"] = period,
        ["AmountKurus"] = kurus,
        ["VatIncluded"] = vat,
        ["EffectiveDate"] = Iso(date ?? TodayInTurkey.AddDays(7))
    };

    private static async Task<AdminResponse> PostAsync(AdminBrowser browser, IReadOnlyDictionary<string, string> form)
    {
        var page = await browser.GetAsync(Page);
        return await browser.PostFormAsync(Page, form, AdminBrowser.AntiforgeryTokenIn(page.Body));
    }

    private BillingPlanPrice AddPriceDirectly(DateOnly date)
    {
        using var db = _central.CreateContext();
        var price = BillingPlanPrice.Create(
            "Starter",
            "Monthly",
            new BillingPrice(50m, "TRY"),
            vatIncluded: true,
            BillingPlanPriceInput.EffectiveFromUtc(date),
            Guid.NewGuid(),
            DateTime.UtcNow);
        db.BillingPlanPrices.Add(price);
        db.SaveChanges();
        return price;
    }

    private List<BillingPlanPrice> StoredPrices()
    {
        using var db = _central.CreateContext();
        return db.BillingPlanPrices.AsNoTracking().ToList();
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
