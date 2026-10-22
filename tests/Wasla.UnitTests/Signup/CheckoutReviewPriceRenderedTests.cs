using System.Globalization;
using System.Net;
using System.Resources;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The checkout summary shows the price checkout would charge, from the Central Admin plan prices (WAS-147), rendered
/// by the real Production host: no price while none is set, then the price in effect with its VAT choice, never a
/// scheduled future price and never a built-in amount. Its own fixture, so the prices it adds stay out of other tests.
/// </summary>
public sealed class CheckoutReviewPriceRenderedTests : IClassFixture<SignupRenderedResponseTests.Hosts>
{
    private static readonly ResourceManager Resources = new("Wasla.Web.Resources.SharedResource", typeof(Wasla.Web.SharedResource).Assembly);
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private readonly SignupRenderedResponseTests.Hosts _hosts;

    public CheckoutReviewPriceRenderedTests(SignupRenderedResponseTests.Hosts hosts) => _hosts = hosts;

    [Fact]
    public async Task TheSummary_ShowsNoPriceUntilOneIsSet_ThenThePriceInEffectWithItsVatChoice()
    {
        var applicant = await _hosts.SeedAsync(PendingRegistrationStatus.AwaitingPayment);
        using var owner = _hosts.OwnerBrowser(_hosts.Production, applicant);

        var before = await owner.GetAsync($"/checkout/review/{applicant.Id}");

        Assert.Equal(HttpStatusCode.OK, before.Status);
        var unpriced = WebUtility.HtmlDecode(before.Body);
        Assert.Contains(Text("Checkout.PriceNotSet"), unpriced, StringComparison.Ordinal);
        Assert.DoesNotContain(" TRY", unpriced, StringComparison.Ordinal);

        // The registration is Starter, Monthly. Another plan's price and a future Starter price must not show.
        await _hosts.AddPlanPriceAsync(Price("Starter", 123.45m, vatIncluded: false, DateTime.UtcNow.AddDays(-1)));
        await _hosts.AddPlanPriceAsync(Price("Starter", 999.99m, vatIncluded: true, DateTime.UtcNow.AddDays(1)));
        await _hosts.AddPlanPriceAsync(Price("Pro", 555.55m, vatIncluded: true, DateTime.UtcNow.AddDays(-1)));

        var after = WebUtility.HtmlDecode((await owner.GetAsync($"/checkout/review/{applicant.Id}")).Body);

        Assert.Contains("123,45 TRY", after, StringComparison.Ordinal);
        Assert.Contains(Text("Billing.VatExcluded"), after, StringComparison.Ordinal);
        Assert.DoesNotContain(Text("Checkout.PriceNotSet"), after, StringComparison.Ordinal);
        Assert.DoesNotContain("999,99", after, StringComparison.Ordinal);
        Assert.DoesNotContain("555,55", after, StringComparison.Ordinal);
    }

    private static BillingPlanPrice Price(string plan, decimal amount, bool vatIncluded, DateTime effectiveFromUtc) =>
        BillingPlanPrice.Create(plan, "Monthly", new BillingPrice(amount, "TRY"), vatIncluded, effectiveFromUtc, Guid.NewGuid(), DateTime.UtcNow);

    private static string Text(string key) => Resources.GetString(key, Turkish) ?? throw new InvalidOperationException(key);
}
