using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Branding;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Branding;

/// <summary>
/// WAS-127 through the real tenant application (<see cref="TenantSessionWebHost"/>: real tenant resolution for two tenant
/// hosts, the real cookie scheme and role policies, the real controllers and views, one SQLite database per tenant):
/// only an Owner manages the logo on Account Settings, every Live Screen role sees it, a tenant only ever gets its own
/// logo, refused uploads say why in the user's language, and a replaced or removed logo is never served from cache to a
/// freshly rendered page.
/// </summary>
public sealed class RestaurantLogoWebTests : IAsyncLifetime
{
    private static readonly Regex HeaderLogo = new(
        "<img class=\"wasla-live-display-identity__logo\" src=\"/restaurant-logo\\?v=([0-9a-f]{32})\"", RegexOptions.Compiled);

    private static readonly Regex HeaderMark = new(
        "<span class=\"wasla-live-display-identity__mark wasla-live-display-identity__mark--logo (wasla-logo-bg--[a-z-]+)\"", RegexOptions.Compiled);

    private static readonly Regex CheckedBackgroundInput = new(
        "<input[^>]*name=\"background\"[^>]*value=\"([a-z-]+)\"[^>]*checked=\"checked\"", RegexOptions.Compiled);

    private static readonly Regex NotificationPayload = new(@"var data = (\{.*?\});", RegexOptions.Compiled | RegexOptions.Singleline);

    private TenantSessionWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantSessionWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task AnOwner_UploadsReplacesAndRemovesTheLogo_AndTheLiveScreenFollowsWithoutStaleCache()
    {
        _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner);
        using var owner = _host.Client(TenantSessionWebHost.AlphaHost);
        await owner.LoginAsync("owner@alpha.test");

        // No logo: the storefront icon, and no image request at all.
        var before = await HtmlAsync(owner, "/orders/live-display");
        Assert.DoesNotMatch(HeaderLogo, before);
        Assert.Contains("<span class=\"wasla-live-display-identity__mark\" aria-hidden=\"true\"><i class=\"bi bi-shop\"></i></span>", before);
        Assert.DoesNotContain("/restaurant-logo", before);

        var first = TestImages.Png(64, 64);
        AssertRedirectsTo(await UploadAsync(owner, first), "/settings/account");
        var settings = await HtmlAsync(owner, "/settings/account");
        Assert.Equal(Resource("en-US", "Settings.RestaurantLogo.Saved"), Notification(settings).Success);
        var v1 = HeaderLogoVersion(await HtmlAsync(owner, "/orders/live-display"));
        Assert.Contains($"src=\"/restaurant-logo?v={v1}\"", settings);

        var image = await owner.GetAsync($"/restaurant-logo?v={v1}");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(first, await image.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("private, max-age=31536000, immutable", CacheControl(image));
        Assert.Equal("nosniff", Header(image, "X-Content-Type-Options"));
        Assert.Equal("default-src 'none'; sandbox", Header(image, "Content-Security-Policy"));

        // A replacement, sent with a hostile name and media type: both are ignored, the bytes decide.
        var second = TestImages.Png(64, 64, red: 0x20, green: 0x40, blue: 0x60);
        AssertRedirectsTo(await UploadAsync(owner, second, fileName: "..\\..\\wwwroot\\index.svg", contentType: "image/svg+xml"), "/settings/account");
        var v2 = HeaderLogoVersion(await HtmlAsync(owner, "/orders/live-display"));
        Assert.NotEqual(v1, v2);

        var current = await owner.GetAsync($"/restaurant-logo?v={v2}");
        Assert.Equal("image/png", current.Content.Headers.ContentType?.MediaType);
        Assert.Equal(second, await current.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        // The old URL now serves the new image and may not be cached without asking again.
        var old = await owner.GetAsync($"/restaurant-logo?v={v1}");
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        Assert.Equal(second, await old.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("private, no-cache", CacheControl(old));
        var revalidated = await owner.SendAsync(HttpMethod.Get, "/restaurant-logo", null, [new("If-None-Match", $"\"{v2}\"")]);
        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);

        AssertRedirectsTo(await RemoveAsync(owner), "/settings/account");
        Assert.Equal(Resource("en-US", "Settings.RestaurantLogo.Removed"), Notification(await HtmlAsync(owner, "/settings/account")).Success);
        var after = await HtmlAsync(owner, "/orders/live-display");
        Assert.DoesNotMatch(HeaderLogo, after);
        Assert.Contains("<i class=\"bi bi-shop\"></i>", after);
        var removed = await owner.GetAsync($"/restaurant-logo?v={v2}");
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.Equal("no-store", CacheControl(removed));
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Viewer)]
    public async Task OtherRoles_SeeTheLogo_ButCannotUploadReplaceOrRemoveIt(UserRole role)
    {
        _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner);
        _host.SeedUser(_host.AlphaId, "staff@alpha.test", role);
        using var owner = _host.Client(TenantSessionWebHost.AlphaHost);
        await owner.LoginAsync("owner@alpha.test");
        var logo = TestImages.Png(32, 32);
        AssertRedirectsTo(await UploadAsync(owner, logo), "/settings/account");
        var version = await CurrentVersionAsync(_host.AlphaId);

        using var staff = _host.Client(TenantSessionWebHost.AlphaHost);
        await staff.LoginAsync("staff@alpha.test");

        Assert.Equal(version, HeaderLogoVersion(await HtmlAsync(staff, "/orders/live-display")));
        var image = await staff.GetAsync($"/restaurant-logo?v={version}");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(logo, await image.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        // With a valid antiforgery token, so only the role decides.
        AssertRedirectsTo(await staff.GetAsync("/settings/account"), "/auth/access-denied");
        AssertRedirectsTo(await UploadAsync(staff, TestImages.Png(32, 32, red: 0, green: 0, blue: 0)), "/auth/access-denied");
        AssertRedirectsTo(await RemoveAsync(staff), "/auth/access-denied");
        AssertRedirectsTo(await SetBackgroundAsync(staff, "dark"), "/auth/access-denied");

        Assert.Equal(version, await CurrentVersionAsync(_host.AlphaId));
        Assert.Equal(RestaurantLogoBackground.ThemeAware, await CurrentBackgroundAsync(_host.AlphaId));
    }

    [Fact]
    public async Task ATenant_OnlyEverGetsItsOwnLogo()
    {
        _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner);
        _host.SeedUser(_host.BetaId, "owner@beta.test", UserRole.Owner);
        using var alpha = _host.Client(TenantSessionWebHost.AlphaHost);
        await alpha.LoginAsync("owner@alpha.test");
        AssertRedirectsTo(await UploadAsync(alpha, TestImages.Png(32, 32)), "/settings/account");
        var alphaVersion = await CurrentVersionAsync(_host.AlphaId);

        // Beta has no logo, whatever version its URL names.
        using var beta = _host.Client(TenantSessionWebHost.BetaHost);
        await beta.LoginAsync("owner@beta.test");
        Assert.Equal(HttpStatusCode.NotFound, (await beta.GetAsync($"/restaurant-logo?v={alphaVersion}")).StatusCode);
        Assert.DoesNotMatch(HeaderLogo, await HtmlAsync(beta, "/orders/live-display"));

        // Alpha's session replayed on Beta's host is no session there.
        using var replay = alpha.ReplayTo(_host.BaseAddress, TenantSessionWebHost.BetaHost);
        var replayed = await replay.GetAsync($"/restaurant-logo?v={alphaVersion}");
        Assert.Equal(HttpStatusCode.Redirect, replayed.StatusCode);
        Assert.StartsWith("/auth/login", PathOf(replayed));

        // Beta's own upload, background and removal leave Alpha's logo alone.
        var betaLogo = TestImages.Png(40, 20, red: 0, green: 0x80, blue: 0);
        AssertRedirectsTo(await UploadAsync(beta, betaLogo), "/settings/account");
        Assert.Equal(alphaVersion, await CurrentVersionAsync(_host.AlphaId));
        AssertRedirectsTo(await SetBackgroundAsync(beta, "dark"), "/settings/account");
        Assert.Equal(RestaurantLogoBackground.Dark, await CurrentBackgroundAsync(_host.BetaId));
        Assert.Equal(RestaurantLogoBackground.ThemeAware, await CurrentBackgroundAsync(_host.AlphaId));
        Assert.Equal("wasla-logo-bg--theme-aware", HeaderBackgroundClass(await HtmlAsync(alpha, "/orders/live-display")));
        Assert.Equal(betaLogo, await (await beta.GetAsync("/restaurant-logo")).Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        AssertRedirectsTo(await RemoveAsync(beta), "/settings/account");
        Assert.Equal(alphaVersion, await CurrentVersionAsync(_host.AlphaId));
        Assert.Null(await CurrentVersionAsync(_host.BetaId));
    }

    [Fact]
    public async Task WithoutASession_TheLogoIsNotServed()
    {
        _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner);
        using var owner = _host.Client(TenantSessionWebHost.AlphaHost);
        await owner.LoginAsync("owner@alpha.test");
        AssertRedirectsTo(await UploadAsync(owner, TestImages.Png(32, 32)), "/settings/account");

        using var anonymous = _host.Client(TenantSessionWebHost.AlphaHost);
        var response = await anonymous.GetAsync("/restaurant-logo");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/auth/login", PathOf(response));
        Assert.NotEqual("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    public static TheoryData<string, string, string, string> RefusedUploads()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var culture in new[] { "tr-TR", "en-US", "ar-SA", "ru-RU" })
        {
            data.Add(culture, "svg", "Settings.RestaurantLogo.Error.UnsupportedFormat", "");
            data.Add(culture, "html-named-png", "Settings.RestaurantLogo.Error.UnsupportedFormat", "");
            data.Add(culture, "truncated-png", "Settings.RestaurantLogo.Error.Invalid", "");
            data.Add(culture, "too-large", "Settings.RestaurantLogo.Error.TooLarge", "1");
            data.Add(culture, "too-many-pixels", "Settings.RestaurantLogo.Error.Dimensions", "4096");
            data.Add(culture, "empty", "Settings.RestaurantLogo.Error.Required", "");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedUploads))]
    public async Task ARefusedUpload_SaysWhyInTheUsersLanguage_AndKeepsTheCurrentLogo(string culture, string upload, string messageKey, string argument)
    {
        _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner);
        using var owner = _host.Client(TenantSessionWebHost.AlphaHost);
        await owner.LoginAsync("owner@alpha.test");
        owner.Cookies[CookieRequestCultureProvider.DefaultCookieName] =
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)));
        AssertRedirectsTo(await UploadAsync(owner, TestImages.Png(32, 32)), "/settings/account");
        await HtmlAsync(owner, "/settings/account");
        var version = await CurrentVersionAsync(_host.AlphaId);

        var content = upload switch
        {
            "svg" => "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>"u8.ToArray(),
            "html-named-png" => "<!DOCTYPE html><script>alert(1)</script>"u8.ToArray(),
            "truncated-png" => TestImages.Png(32, 32)[..^12],
            "too-large" => TestImages.Png(32, 32).Concat(new byte[RestaurantLogoImage.MaxBytes]).ToArray(),
            "too-many-pixels" => TestImages.JpegHeader(RestaurantLogoImage.MaxDimension + 1, 100),
            _ => Array.Empty<byte>()
        };
        AssertRedirectsTo(await UploadAsync(owner, content, fileName: "logo.png", contentType: "image/png"), "/settings/account");

        var page = await HtmlAsync(owner, "/settings/account");
        var error = Notification(page).Error;
        Assert.Equal(Resource(culture, messageKey).Replace("{0}", argument, StringComparison.Ordinal), error);
        Assert.DoesNotContain(":\\", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", error, StringComparison.Ordinal);
        Assert.Equal(version, await CurrentVersionAsync(_host.AlphaId));
        Assert.Contains($"src=\"/restaurant-logo?v={version}\"", page);
    }

    [Fact]
    public async Task AnOwner_ChoosesEachBackground_TheLiveScreenUsesIt_AndTheLogoStaysTheSame()
    {
        _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner);
        using var owner = _host.Client(TenantSessionWebHost.AlphaHost);
        await owner.LoginAsync("owner@alpha.test");
        var logo = TestImages.Png(48, 48);
        AssertRedirectsTo(await UploadAsync(owner, logo), "/settings/account");
        await HtmlAsync(owner, "/settings/account");
        var version = await CurrentVersionAsync(_host.AlphaId);

        // A new logo is Theme-aware, and the settings page marks it as chosen.
        Assert.Equal(RestaurantLogoBackground.ThemeAware, await CurrentBackgroundAsync(_host.AlphaId));
        Assert.Equal("wasla-logo-bg--theme-aware", HeaderBackgroundClass(await HtmlAsync(owner, "/orders/live-display")));
        Assert.Equal("theme-aware", CheckedBackground(await HtmlAsync(owner, "/settings/account")));

        foreach (var (token, value) in new[]
                 {
                     ("transparent", RestaurantLogoBackground.Transparent), ("light", RestaurantLogoBackground.Light),
                     ("dark", RestaurantLogoBackground.Dark), ("theme-aware", RestaurantLogoBackground.ThemeAware)
                 })
        {
            AssertRedirectsTo(await SetBackgroundAsync(owner, token), "/settings/account");
            var settings = await HtmlAsync(owner, "/settings/account");
            Assert.Equal(Resource("en-US", "Settings.RestaurantLogo.Background.Saved"), Notification(settings).Success);
            Assert.Equal(token, CheckedBackground(settings));
            Assert.Equal(value, await CurrentBackgroundAsync(_host.AlphaId));

            // Only the class changes: the same image under the same URL, so nothing is uploaded or fetched again.
            var live = await HtmlAsync(owner, "/orders/live-display");
            Assert.Equal("wasla-logo-bg--" + token, HeaderBackgroundClass(live));
            Assert.Equal(version, HeaderLogoVersion(live));
            Assert.Equal(version, await CurrentVersionAsync(_host.AlphaId));
            var image = await owner.GetAsync($"/restaurant-logo?v={version}");
            Assert.Equal(logo, await image.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }

        // Replacing keeps the chosen style; removing ends it with the logo, and the next logo is Theme-aware again.
        AssertRedirectsTo(await SetBackgroundAsync(owner, "dark"), "/settings/account");
        AssertRedirectsTo(await UploadAsync(owner, TestImages.Png(48, 48, red: 1, green: 2, blue: 3)), "/settings/account");
        Assert.Equal(RestaurantLogoBackground.Dark, await CurrentBackgroundAsync(_host.AlphaId));
        AssertRedirectsTo(await RemoveAsync(owner), "/settings/account");
        Assert.Null(await CurrentBackgroundAsync(_host.AlphaId));
        Assert.DoesNotContain("wasla-logo-bg--", await HtmlAsync(owner, "/orders/live-display"));
        AssertRedirectsTo(await UploadAsync(owner, logo), "/settings/account");
        Assert.Equal(RestaurantLogoBackground.ThemeAware, await CurrentBackgroundAsync(_host.AlphaId));
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("THEME-AWARE")]
    [InlineData("ThemeAware")]
    [InlineData("1")]
    [InlineData("99")]
    [InlineData("purple")]
    [InlineData(" dark")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnUnknownBackground_IsRefused_AndTheCurrentOneIsKept(string? posted)
    {
        _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner);
        using var owner = _host.Client(TenantSessionWebHost.AlphaHost);
        await owner.LoginAsync("owner@alpha.test");
        AssertRedirectsTo(await UploadAsync(owner, TestImages.Png(32, 32)), "/settings/account");
        AssertRedirectsTo(await SetBackgroundAsync(owner, "light"), "/settings/account");

        var response = await SetBackgroundAsync(owner, posted);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RestaurantLogoBackground.Light, await CurrentBackgroundAsync(_host.AlphaId));
    }

    [Fact]
    public async Task ABackgroundWithoutAnAntiforgeryToken_IsRefused()
    {
        _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner);
        using var owner = _host.Client(TenantSessionWebHost.AlphaHost);
        await owner.LoginAsync("owner@alpha.test");
        AssertRedirectsTo(await UploadAsync(owner, TestImages.Png(32, 32)), "/settings/account");

        var response = await owner.PostFormAsync("/settings/account/logo/background", [new("background", "dark")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RestaurantLogoBackground.ThemeAware, await CurrentBackgroundAsync(_host.AlphaId));
    }

    [Fact]
    public async Task ABackgroundWithoutALogo_StoresNothing_AndSaysSo()
    {
        _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner);
        using var owner = _host.Client(TenantSessionWebHost.AlphaHost);
        await owner.LoginAsync("owner@alpha.test");

        AssertRedirectsTo(await SetBackgroundAsync(owner, "dark"), "/settings/account");

        Assert.Equal(Resource("en-US", "Settings.RestaurantLogo.Background.NoLogo"), Notification(await HtmlAsync(owner, "/settings/account")).Error);
        Assert.Null(await CurrentBackgroundAsync(_host.AlphaId));
    }

    private async Task<string?> CurrentVersionAsync(Guid tenantId)
    {
        using var scope = _host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IRestaurantLogoService>().GetCurrentAsync(tenantId, TestContext.Current.CancellationToken))?.Version;
    }

    private async Task<RestaurantLogoBackground?> CurrentBackgroundAsync(Guid tenantId)
    {
        using var scope = _host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IRestaurantLogoService>().GetCurrentAsync(tenantId, TestContext.Current.CancellationToken))?.Background;
    }

    private static async Task<HttpResponseMessage> SetBackgroundAsync(TenantSessionClient client, string? background)
    {
        var token = await client.AntiforgeryTokenAsync("/orders/live-display");
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        if (background is not null)
            form.Add(new("background", background));
        return await client.PostFormAsync("/settings/account/logo/background", form);
    }

    private static string HeaderBackgroundClass(string html)
    {
        var match = HeaderMark.Match(html);
        Assert.True(match.Success, "The Live Screen header shows no restaurant logo box.");
        return match.Groups[1].Value;
    }

    private static string? CheckedBackground(string html)
    {
        var match = CheckedBackgroundInput.Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        TenantSessionClient client, byte[] content, string fileName = "logo.png", string contentType = "image/png")
    {
        // The Live Screen renders a token for every role, so a refusal below is the role's, not the token's.
        var token = await client.AntiforgeryTokenAsync("/orders/live-display");
        var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" }
        };
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "logo", fileName);
        return await client.SendAsync(HttpMethod.Post, "/settings/account/logo", form);
    }

    private static async Task<HttpResponseMessage> RemoveAsync(TenantSessionClient client)
    {
        var token = await client.AntiforgeryTokenAsync("/orders/live-display");
        return await client.PostFormAsync("/settings/account/logo/remove", [new("__RequestVerificationToken", token)]);
    }

    private static async Task<string> HtmlAsync(TenantSessionClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static string HeaderLogoVersion(string html)
    {
        var match = HeaderLogo.Match(html);
        Assert.True(match.Success, "The Live Screen header shows no restaurant logo.");
        return match.Groups[1].Value;
    }

    private static (string? Success, string? Error) Notification(string html)
    {
        var match = NotificationPayload.Match(html);
        Assert.True(match.Success, "The page shows no notification.");
        using var payload = JsonDocument.Parse(match.Groups[1].Value);
        return (payload.RootElement.GetProperty("success").GetString(), payload.RootElement.GetProperty("error").GetString());
    }

    // As sent; HttpClient's parsed value reorders the directives.
    private static string CacheControl(HttpResponseMessage response) => response.Headers.NonValidated["Cache-Control"].ToString();

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    private static string PathOf(HttpResponseMessage response)
    {
        var location = response.Headers.Location!;
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    private static void AssertRedirectsTo(HttpResponseMessage response, string path)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = PathOf(response);
        Assert.True(location == path || location.StartsWith(path + "?", StringComparison.Ordinal), $"Redirected to {location}, not {path}.");
    }

    private static string Resource(string culture, string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault() ?? $"[missing {key} in {culture}]";
}
