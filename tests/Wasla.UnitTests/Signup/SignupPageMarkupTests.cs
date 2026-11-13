using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Localization;
using Microsoft.Data.Sqlite;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The signup page's markup and resources (WAS-194), rendered by the real controllers and views of a
/// <see cref="SignupWebHost"/>: one H1, the five steps in flow order, the receipt marking the open step, the SMS code
/// field's attributes, the address fields' searchable lists and required markers, the address before the contact
/// details, the draft's radios kept out of the browser's form restore, the password Show controls, the no-script notice, the draft and
/// endpoints the script reads, truthful money wording per
/// environment, and every resource key the signup views and the draft API use present in all five resource files.
/// Browser behaviour is in <see cref="SignupFlowBrowserTests"/>.
/// </summary>
public sealed partial class SignupPageMarkupTests : IAsyncLifetime
{
    private static readonly ResourceManager Resources = new("Wasla.Web.Resources.SharedResource", typeof(Wasla.Web.SharedResource).Assembly);
    private static readonly string[] ResourceFiles =
        ["SharedResource.resx", "SharedResource.tr-TR.resx", "SharedResource.en-US.resx", "SharedResource.ar-SA.resx", "SharedResource.ru-RU.resx"];

    // The codes the draft API answers with (docs/architecture/signup-registration.md); the page shows their messages.
    private static readonly string[] ApiKeys =
    [
        "Signup.Draft.Missing", "Signup.Draft.Submitted", "Signup.Draft.ValidationFailed", "Signup.Draft.Incomplete",
        "Signup.Draft.InvalidRequest", "Signup.Phone.OwnerIncomplete", "Signup.Phone.AlreadyVerified", "Signup.Phone.NotVerified",
        "Signup.Phone.CodeIncorrect", "Signup.Phone.CodeExpired", "Signup.Phone.CodeFormatInvalid", "Signup.Phone.NoActiveCode",
        "Signup.Phone.ResendNotAvailable", "Signup.Phone.Locked", "Signup.Phone.SendLimitReached", "Signup.Phone.DeliveryFailed",
        "Signup.Phone.Unavailable", "Signup.Phone.CapacityReached", "Validation.MobilePhoneRequired", "Validation.MobilePhoneInvalid",
        "Signup.TooManyAttempts", "Signup.SlugUnavailable", "Signup.EnterpriseContactOnly", "Signup.ProvisioningFailed",
        "Validation.BusinessPhoneTooLong"
    ];

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-signup-markup-keys-").FullName;
    private SignupWebHost? _host;

    public async ValueTask InitializeAsync()
    {
        _connection.Open();
        await SignupWebHost.CreateCentralDbAsync(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        _connection.Dispose();
        try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<string> Cultures => new() { "tr-TR", "en-US", "ar-SA", "ru-RU" };

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ThePage_HasOneH1_FiveStepsInOrder_AndTheReceiptMarksTheOpenStep(string culture)
    {
        var page = await GetAsync("Production", culture);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Single(Regex.Matches(page.Body, "<h1[ >]"));
        Assert.Equal(new[] { "plan", "restaurant", "owner", "phone", "review" },
            StepSection().Matches(page.Body).Select(m => m.Groups["id"].Value).ToArray());
        // Only the open step is shown before the script runs: a new visitor starts at the plan.
        Assert.Contains("data-su-current=\"plan\"", page.Body);
        Assert.Single(VisibleStep().Matches(page.Body));
        // The aside and the mobile sheet each mark the open step for assistive technology.
        Assert.Equal(2, Regex.Matches(page.Body, "data-su-receipt-step=\"plan\" data-state=\"current\" aria-current=\"step\"").Count);
        Assert.Contains("<noscript>", page.Body);
        Assert.Contains(Text("Signup.Flow.NoScript", culture), WebUtility.HtmlDecode(page.Body));
        Assert.Equal(culture == "ar-SA" ? "rtl" : "ltr", Regex.Match(page.Body, "<html lang=\"[^\"]+\" dir=\"(?<dir>[a-z]+)\"").Groups["dir"].Value);
    }

    [Fact]
    public async Task TheCodeField_TakesSixDigitsFromTheKeyboardSuggestion_AndStaysLeftToRight()
    {
        var page = await GetAsync("Production", "ar-SA");

        var input = Regex.Match(page.Body, "<input class=\"su-code__input\"[^>]*>", RegexOptions.Singleline).Value;
        Assert.Contains("id=\"signupPhoneCode\"", input);
        Assert.Contains("inputmode=\"numeric\"", input);
        Assert.Contains("autocomplete=\"one-time-code\"", input);
        Assert.Contains("enterkeyhint=\"done\"", input);
        Assert.Contains("maxlength=\"6\"", input);
        Assert.Contains("dir=\"ltr\"", input);
        Assert.Contains("aria-describedby=\"signupPhoneCode-error\"", input);
        Assert.Contains("<label class=\"su-label\" for=\"signupPhoneCode\">", page.Body);
        Assert.Contains("id=\"signupPhoneCode-error\" role=\"alert\"", page.Body);
    }

    // The four address fields are searchable lists (signup-combobox.js) with their own labels. City and district keep a
    // native select, out of sight and out of the tab order, as the value; neighbourhood and street are the inputs
    // themselves. The neighbourhood is required like the city, district and street; the building details are optional.
    [Fact]
    public async Task TheAddressFields_AreLabelledSearchableLists_AndOnlyTheBuildingDetailsAreOptional()
    {
        var page = await GetAsync("Production", "tr-TR");
        var body = WebUtility.HtmlDecode(page.Body);

        Assert.Equal(new[] { "city", "district", "neighborhood", "street" },
            Regex.Matches(page.Body, "data-su-combo=\"(?<field>[a-z]+)\"").Select(m => m.Groups["field"].Value).ToArray());
        Assert.DoesNotContain("<datalist", page.Body);
        foreach (var (input, list) in new[] { ("signupCityInput", "signupCityList"), ("signupDistrictInput", "signupDistrictList"),
                     ("signupNeighborhood", "signupNeighborhoodList"), ("signupStreetAddress", "signupStreetList") })
        {
            var label = Regex.Match(page.Body, $"<label class=\"su-label\" for=\"{input}\">(?<text>.*?)</label>", RegexOptions.Singleline);
            Assert.True(label.Success, $"No label for {input}.");
            Assert.DoesNotContain("su-optional", label.Groups["text"].Value);
            Assert.Contains("aria-required=\"true\"", Regex.Match(page.Body, $"<input[^>]*id=\"{input}\"[^>]*>", RegexOptions.Singleline).Value);
            Assert.Matches($"<ul class=\"su-combo__list\" role=\"listbox\" id=\"{list}\" aria-label=\"[^\"]+\">", page.Body);
        }
        foreach (var select in new[] { "signupCityId", "signupDistrictId" })
        {
            var tag = Regex.Match(page.Body, $"<select[^>]*id=\"{select}\"[^>]*>", RegexOptions.Singleline).Value;
            Assert.Contains("tabindex=\"-1\"", tag);
            Assert.Contains("aria-hidden=\"true\"", tag);
            Assert.Contains("data-val-required=", tag);
        }
        foreach (var input in new[] { "signupNeighborhood", "signupStreetAddress" })
            Assert.Contains("data-val-required=", Regex.Match(page.Body, $"<input[^>]*id=\"{input}\"[^>]*>", RegexOptions.Singleline).Value);
        foreach (var optional in new[] { "signupBuildingNumber", "signupFloor", "signupDoorNumber", "signupPostalCode", "signupAddressNote", "signupLocationUrl" })
            Assert.Matches($"<label class=\"su-label\" for=\"{optional}\">[^<]*<span class=\"su-optional\">", page.Body);
        Assert.Contains(Text("Signup.Address.NeighborhoodLabel", "tr-TR"), body);
    }

    // The restaurant step asks for the address before the contact details, so the city is chosen before the landline
    // (its hint names the city's area code), in the page's order, which is also the tab and reading order. The radios
    // the draft decides stay out of the browser's form restore, and each password field has one Show control at its end.
    [Theory]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public async Task TheAddressComesBeforeTheContact_TheDraftsRadiosStayOutOfTheFormRestore_AndEachPasswordHasOneShowControl(string culture)
    {
        var page = await GetAsync("Production", culture);
        var body = WebUtility.HtmlDecode(page.Body);

        var restaurant = Regex.Match(body, "<section class=\"su-step\" id=\"signupStep-restaurant\".*?</section>", RegexOptions.Singleline).Value;
        Assert.Equal(
            new[] { "Signup.Flow.SectionBusiness", "Signup.Flow.SectionPanelAddress", "Signup.StepAddress", "Signup.StepContact" }.Select(key => Text(key, culture)).ToArray(),
            Regex.Matches(restaurant, "<h3 class=\"su-section__title\">(?<text>[^<]*)</h3>").Select(m => m.Groups["text"].Value.Trim()).ToArray());
        Assert.True(restaurant.IndexOf("id=\"signupStreetAddress\"", StringComparison.Ordinal) < restaurant.IndexOf("id=\"signupBusinessPhoneType-Mobile\"", StringComparison.Ordinal));

        foreach (var name in new[] { "BillingPeriod", "PlanCode", "BusinessPhoneType" })
        {
            var radios = Regex.Matches(page.Body, $"<input(?=[^>]*type=\"radio\")(?=[^>]*name=\"{name}\")[^>]*>", RegexOptions.Singleline);
            Assert.True(radios.Count >= 2, $"Only {radios.Count} {name} radios found.");
            Assert.All(radios, radio => Assert.Contains("autocomplete=\"off\"", radio.Value));
        }

        foreach (var input in new[] { "signupPassword", "signupConfirmPassword" })
        {
            var reveal = Regex.Match(body, $"<div class=\"su-reveal\">\\s*<input[^>]*id=\"{input}\"[^>]*>\\s*<button (?<attributes>[^>]*)>(?<text>[^<]*)</button>\\s*</div>", RegexOptions.Singleline);
            Assert.True(reveal.Success, $"No Show control at the end of {input}.");
            var attributes = reveal.Groups["attributes"].Value;
            Assert.Contains("type=\"button\"", attributes);
            Assert.Contains($"aria-controls=\"{input}\"", attributes);
            Assert.Contains($"aria-label=\"{Text("Common.ShowPassword", culture)}\"", attributes);
            Assert.Equal(Text("Signup.Password.Show", culture), reveal.Groups["text"].Value.Trim());
        }
        Assert.DoesNotContain("bi-eye", Regex.Match(body, "<section class=\"su-step\" id=\"signupStep-owner\".*?</section>", RegexOptions.Singleline).Value);
    }

    [Fact]
    public async Task TheScript_GetsTheDraftAndTheApiAddresses_AndTheFormPostsNothingByItself()
    {
        var page = await GetAsync("Production", "tr-TR");

        using var draft = JsonDocument.Parse(WebUtility.HtmlDecode(Script(page.Body, "signupDraftState")));
        Assert.False(draft.RootElement.GetProperty("exists").GetBoolean());
        Assert.Equal("notSent", draft.RootElement.GetProperty("phone").GetProperty("state").GetString());

        using var endpoints = JsonDocument.Parse(WebUtility.HtmlDecode(Script(page.Body, "signupEndpoints")));
        Assert.Equal("/signup/draft/submit", endpoints.RootElement.GetProperty("submit").GetString());
        Assert.Equal("/signup/address-availability", endpoints.RootElement.GetProperty("addressAvailability").GetString());
        Assert.Equal(JsonValueKind.Null, endpoints.RootElement.GetProperty("fakeSmsOutbox").ValueKind);

        // The token the script sends in its header, and the one submit button (Enter's implicit submission).
        Assert.Contains("name=\"__RequestVerificationToken\"", page.Body);
        Assert.Single(Regex.Matches(page.Body, "type=\"submit\""));
        Assert.Contains("id=\"signupSubmitButton\"", page.Body);
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    public async Task DueTodayAndAutomaticRenewal_AreSaidOnlyWhereAPaymentStepCanFollow(string environment, bool simulator)
    {
        var page = await GetAsync(environment, "tr-TR");
        var text = WebUtility.HtmlDecode(page.Body);

        Assert.Equal(simulator, text.Contains(Text("Signup.Receipt.DueToday", "tr-TR"), StringComparison.Ordinal));
        Assert.Equal(!simulator, text.Contains(Text("Signup.Receipt.PlanPrice", "tr-TR"), StringComparison.Ordinal));
        Assert.Equal(simulator, text.Contains(Text("Signup.Pay.AutoRenew", "tr-TR"), StringComparison.Ordinal));
        // The support address comes from configuration (default destek@wasla.tr), never the old placeholder.
        Assert.Contains("href=\"mailto:destek@wasla.tr\"", page.Body);
        Assert.DoesNotContain("support@wasla.local", page.Body);
    }

    [Fact]
    public void EveryKeyTheSignupViewsAndTheDraftApiUse_IsInAllFiveResourceFiles()
    {
        var web = Path.Combine(Root(), "src", "Wasla.Web");
        var views = new[] { Path.Combine(web, "Views", "Signup", "Index.cshtml") }
            .Concat(Directory.GetFiles(Path.Combine(web, "Views", "Shared"), "_Signup*.cshtml"));
        var keys = views
            .SelectMany(file => ResourceKey().Matches(File.ReadAllText(file)).Select(m => m.Groups["key"].Value))
            .Concat(ApiKeys)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(keys.Count > 100, $"Only {keys.Count} keys found; the pattern no longer matches the views.");

        foreach (var file in ResourceFiles)
        {
            var names = XDocument.Load(Path.Combine(web, "Resources", file)).Root!.Elements("data")
                .Select(data => (string)data.Attribute("name")!)
                .ToHashSet(StringComparer.Ordinal);
            var missing = keys.Where(key => !names.Contains(key)).Order(StringComparer.Ordinal).ToList();
            Assert.True(missing.Count == 0, $"{file} misses: {string.Join(", ", missing)}");
        }
    }

    private async Task<TestResponse> GetAsync(string environment, string culture)
    {
        if (_host is null || !_hostEnvironment.Equals(environment, StringComparison.Ordinal))
        {
            if (_host is not null)
                await _host.DisposeAsync();
            _host = await SignupWebHost.StartAsync(_connection, _keyRing, environment);
            _hostEnvironment = environment;
        }

        using var browser = _host.NewBrowser();
        browser.SetCookie(
            SignupWebHost.CentralHost,
            CookieRequestCultureProvider.DefaultCookieName,
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))));
        return await browser.GetAsync("/signup");
    }

    private string _hostEnvironment = string.Empty;

    private static string Script(string html, string id)
    {
        var match = Regex.Match(html, $"<script type=\"application/json\" id=\"{id}\">(?<json>.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success, $"No {id} script.");
        return match.Groups["json"].Value;
    }

    private static string Text(string key, string culture) =>
        Resources.GetString(key, CultureInfo.GetCultureInfo(culture)) ?? throw new InvalidOperationException(key);

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Wasla.sln not found above the test output.");
    }

    [GeneratedRegex("<section class=\"su-step\" id=\"signupStep-(?<id>[a-z]+)\"")]
    private static partial Regex StepSection();

    [GeneratedRegex("<section class=\"su-step\"[^>]*data-su-step=\"[a-z]+\"(?![^>]*hidden=)[^>]*>", RegexOptions.Singleline)]
    private static partial Regex VisibleStep();

    // Every resource-key literal in the views, also those chosen in a conditional (L[flag ? "A" : "B"]).
    [GeneratedRegex("\"(?<key>(?:Signup|Validation|Billing|Common|Setup|Pricing)\\.[A-Za-z0-9.]+)\"")]
    private static partial Regex ResourceKey();
}
