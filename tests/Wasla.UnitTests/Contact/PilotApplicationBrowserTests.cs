using System.Globalization;
using System.Resources;
using System.Text.Json;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Auth;
using Wasla.UnitTests.Signup;
using Wasla.Web.Routing;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// The pilot restaurant application (WAS-217) in a real headless Chromium browser over <see cref="TenantSessionWebHost"/>
/// (Production, the real controllers, views, styles and scripts, fake legal facts, Email:Provider Log): the 1440 px split
/// layout with its sticky photo panel, the 390 px phone layout, Arabic right to left, long Russian text, keyboard order
/// and focus, the error summary, one stored application for a double click, and the coming-soon call to action. Every
/// wait is on page state.
/// </summary>
[Trait("Category", "Browser")]
public sealed class PilotApplicationBrowserTests : IAsyncLifetime
{
    // A *.localhost marketing host: Chromium resolves it to loopback itself.
    private const string Host = "wasla.localhost";
    private static readonly ResourceManager Resources = new("Wasla.Web.Resources.SharedResource", typeof(Wasla.Web.SharedResource).Assembly);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    // Elements outside the viewport sideways (a visually hidden element is clipped to 1 px and excluded).
    private const string Overflow = """
        (() => {
          const w = document.documentElement.clientWidth;
          const out = [];
          for (const e of document.querySelectorAll('body *')) {
            const s = getComputedStyle(e);
            if (s.display === 'none' || s.visibility === 'hidden' || e.closest('.wm-visually-hidden') || e.closest('[hidden]')) continue;
            const r = e.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) continue;
            if (r.left < -1 || r.right > w + 1) out.push((e.id || e.className || e.tagName) + ' ' + Math.round(r.left) + '..' + Math.round(r.right));
          }
          return JSON.stringify({ scroll: document.documentElement.scrollWidth, width: w, out: out.slice(0, 8) });
        })()
        """;

    private TenantSessionWebHost _host = null!;
    private int _port;

    public async ValueTask InitializeAsync()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Wasla:TenantOnboarding:MarketingBaseDomain"] = Host,
            ["Wasla:TenantOnboarding:ApplicationBaseDomain"] = "app." + Host,
            ["Wasla:PilotApplication:Enabled"] = "true",
            ["AuthenticationRateLimiting:PilotApplication:PermitLimit"] = "1000",
            ["AuthenticationRateLimiting:PilotApplicationStore:PermitLimit"] = "1000"
        };
        var notice = PilotApplicationModelTests.CompleteNotice();
        foreach (var property in typeof(PilotPrivacyNoticeOptions).GetProperties())
            settings[$"Wasla:PilotApplication:PrivacyNotice:{property.Name}"] = property.GetValue(notice) as string;
        _host = await TenantSessionWebHost.StartAsync(configuration: settings);
        _port = _host.BaseAddress.Port;

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        // The complete catalog: the form stays closed while the city catalog lacks any of the 81 provinces.
        db.Cities.AddRange(PilotProvinceSeed.All(idOffset: 1000));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Desktop1440_SplitLayout_KeyboardOrder_ErrorFocus_AndADoubleClickStoresOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, mobile: false, ct);
        await OpenAsync(browser, PublicSiteUrls.PilotPath, ct);

        // The sticky photo panel: 600 px wide, one screen tall, its photo loaded; the form beside it; nothing sideways.
        var layout = await Json(browser, """
            (() => {
              const aside = document.querySelector('.wm-pilot__aside').getBoundingClientRect();
              const main = document.querySelector('.wm-pilot__main').getBoundingClientRect();
              const img = document.querySelector('.wm-pilot__photo img');
              return JSON.stringify({ asideWidth: aside.width, asideHeight: aside.height, inner: innerHeight, mainLeft: main.left,
                position: getComputedStyle(document.querySelector('.wm-pilot__aside')).position,
                photo: img.complete && img.naturalWidth > 0, h1: document.querySelectorAll('h1').length,
                stepsAside: getComputedStyle(document.querySelector('.wm-pilot-steps--aside')).display,
                stepsBelow: getComputedStyle(document.querySelector('.wm-pilot-steps--below')).display });
            })()
            """, ct);
        Assert.Equal(600, layout.GetProperty("asideWidth").GetDouble(), 1);
        Assert.Equal(layout.GetProperty("inner").GetDouble(), layout.GetProperty("asideHeight").GetDouble(), 1);
        Assert.Equal("sticky", layout.GetProperty("position").GetString());
        Assert.True(layout.GetProperty("photo").GetBoolean());
        Assert.Equal(1, layout.GetProperty("h1").GetInt32());
        Assert.Equal(("grid", "none"), (layout.GetProperty("stepsAside").GetString(), layout.GetProperty("stepsBelow").GetString()));
        await AssertNoSidewaysOverflowAsync(browser, ct);

        // Tab order: the form's controls in reading order, then the privacy notice, then the submit button.
        var order = await TabOrderAsync(browser, 24, ct);
        var expected = new[] { "pilot-restaurant-name", "pilot-city", "pilot-contact-name", "pilot-phone", "pilot-email", "pilot-note", "privacy-link", "submit" };
        var positions = expected.Select(id => order.IndexOf(id)).ToList();
        Assert.True(positions.All(p => p >= 0), $"Tab order: {string.Join(", ", order)}");
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.DoesNotContain("pilot-website", order);

        // An empty submit: the server answers with the summary, which takes the focus.
        await SubmitAsync(browser, "document.querySelector('[data-wm-submit-label]').click()", ct);
        Assert.Equal("pilot-error-summary", await browser.EvaluateAsync<string>("document.activeElement.id", ct));
        Assert.Equal(5, await browser.EvaluateAsync<int>("document.querySelectorAll('.wm-pilot-summary__list a').length", ct));
        Assert.Equal(Text("Pilot.MetaTitleWithErrors", "tr-TR"), await browser.EvaluateAsync<string>("document.title", ct));
        await AssertNoSidewaysOverflowAsync(browser, ct);

        // Typed values, then two clicks on the button: the second is ignored and the server stores one application.
        await TypeAsync(browser, "pilot-restaurant-name", "Lezzet Ocakbaşı", ct);
        await browser.EvaluateAsync<bool>("(() => { const s = document.getElementById('pilot-city'); s.value = '34'; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()", ct);
        await TypeAsync(browser, "pilot-contact-name", "Ayşe Kaya", ct);
        await TypeAsync(browser, "pilot-phone", "532 418 27 90", ct);
        await TypeAsync(browser, "pilot-email", "ayse@lezzet.test", ct);
        await SubmitAsync(browser, "(() => { const b = document.querySelector('[data-wm-submit-label]'); b.click(); b.click(); })()", ct);

        Assert.Equal("pilot-sent-title", await browser.EvaluateAsync<string>("document.activeElement.id", ct));
        Assert.Equal(Text("Pilot.MetaTitleSent", "tr-TR"), await browser.EvaluateAsync<string>("document.title", ct));
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        var stored = await db.ContactMessages.AsNoTracking().Where(m => m.Topic == ContactTopic.PilotApplication && m.RestaurantName == "Lezzet Ocakbaşı").ToListAsync(ct);
        Assert.Single(stored);
        Assert.Equal(("34", "5324182790"), (stored[0].CityPlateCode, stored[0].Phone));
    }

    [Theory]
    [InlineData("tr-TR", 390, true)]
    [InlineData("ru-RU", 390, true)]
    [InlineData("ar-SA", 390, true)]
    [InlineData("ru-RU", 1440, false)]
    [InlineData("ar-SA", 1440, false)]
    public async Task EveryCulture_FitsThePhoneAndTheDesktop_WithErrorsShown_AndArabicMirrored(string culture, int width, bool mobile)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, mobile ? 844 : 900, mobile, ct);
        await OpenAsync(browser, PublicSiteUrls.PilotPath, ct);

        Assert.Equal(culture == "ar-SA" ? "rtl" : "ltr", await browser.EvaluateAsync<string>("document.documentElement.dir", ct));
        await AssertNoSidewaysOverflowAsync(browser, ct);

        // The longest texts: every field error, the summary and the hints at once.
        await TypeAsync(browser, "pilot-phone", "532 12", ct);
        await TypeAsync(browser, "pilot-email", "not-an-address", ct);
        await SubmitAsync(browser, "document.querySelector('[data-wm-submit-label]').click()", ct);
        Assert.Equal("pilot-error-summary", await browser.EvaluateAsync<string>("document.activeElement.id", ct));
        await AssertNoSidewaysOverflowAsync(browser, ct);

        var state = await Json(browser, """
            JSON.stringify({
              phoneDir: getComputedStyle(document.querySelector('.wm-phone')).direction,
              emailDir: getComputedStyle(document.getElementById('pilot-email')).direction,
              asideLeft: document.querySelector('.wm-pilot__aside').getBoundingClientRect().left,
              mainLeft: document.querySelector('.wm-pilot__main').getBoundingClientRect().left,
              buttonHeight: document.querySelector('[data-wm-submit-label]').getBoundingClientRect().height,
              langAside: getComputedStyle(document.querySelector('.wm-pilot__lang--aside')).display,
              stepsBelow: getComputedStyle(document.querySelector('.wm-pilot-steps--below')).display
            })
            """, ct);
        // The phone number and the email address read left to right in every culture.
        Assert.Equal(("ltr", "ltr"), (state.GetProperty("phoneDir").GetString(), state.GetProperty("emailDir").GetString()));
        // The submit label fits on one line.
        Assert.True(state.GetProperty("buttonHeight").GetDouble() <= 56, $"The submit button wrapped: {state}");
        if (mobile)
        {
            Assert.Equal(("block", "grid"), (state.GetProperty("langAside").GetString(), state.GetProperty("stepsBelow").GetString()));
        }
        else if (culture == "ar-SA")
        {
            // Right to left, the photo panel is on the right of the form.
            Assert.True(state.GetProperty("asideLeft").GetDouble() > state.GetProperty("mainLeft").GetDouble(), $"Not mirrored: {state}");
        }
    }

    [Theory]
    [InlineData("ru-RU", 1440, false)]
    [InlineData("ru-RU", 390, true)]
    [InlineData("ar-SA", 390, true)]
    public async Task ComingSoonPage_ShowsBothActionsOnOneLineEach_AndLeadsToTheForm(string culture, int width, bool mobile)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, mobile ? 844 : 900, mobile, ct);
        await browser.NavigateAsync(new Uri($"http://{Host}:{_port}/"), ct);
        await WaitForAsync(browser, "document.readyState === 'complete' && !!document.querySelector('[data-coming-soon-cta=\"pilot\"]')", ct);

        var buttons = await Json(browser, """
            JSON.stringify([...document.querySelectorAll('[data-coming-soon-cta]')].map(a => ({ h: a.getBoundingClientRect().height, href: a.getAttribute('href') })))
            """, ct);
        Assert.Equal(2, buttons.GetArrayLength());
        Assert.All(buttons.EnumerateArray(), b => Assert.True(b.GetProperty("h").GetDouble() <= 56, $"A call to action wrapped: {buttons}"));
        Assert.Equal("/pilot", buttons[0].GetProperty("href").GetString());
        await AssertNoSidewaysOverflowAsync(browser, ct);
    }

    // The phone field after the fixed +90 holds the 10 national digits only, with the signup page's phone behaviour
    // (signup-phone-format.js): an 11th digit typed is refused where the caret is, letters and symbols never land, the
    // editing keys and Ctrl+A work, a paste is cleaned of spaces, brackets and dashes, loses a +90, 0090 or trunk 0, and
    // keeps its first 10 national digits. Then the form sends the number. Keyboard input is real (CDP), pastes are
    // clipboard events like the signup tests'.
    [Fact]
    public async Task ThePhoneField_HoldsTenNationalDigits_TypedOrPasted_AndTheFormSendsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("tr-TR", 1440, 900, mobile: false, ct);
        await OpenAsync(browser, PublicSiteUrls.PilotPath, ct);
        const string Phone = "document.getElementById('pilot-phone')";
        async Task<string> ValueAsync() => await browser.EvaluateAsync<string>($"{Phone}.value", ct);
        async Task<int> CaretAsync() => await browser.EvaluateAsync<int>($"{Phone}.selectionStart", ct);
        async Task PasteAsync(string text, bool replaceAll = true)
        {
            var json = JsonSerializer.Serialize(text);
            await browser.EvaluateAsync<bool>($$"""
                (() => { const p = {{Phone}}; p.focus(); if ({{(replaceAll ? "true" : "false")}}) p.select(); else p.setSelectionRange(p.value.length, p.value.length);
                  const data = new DataTransfer(); data.setData('text', {{json}});
                  p.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true })); return true; })()
                """, ct);
        }

        var attributes = await Json(browser, $$"""
            (() => { const p = {{Phone}}; return JSON.stringify({ type: p.type, inputmode: p.getAttribute('inputmode'), maxlength: p.getAttribute('maxlength'),
              autocomplete: p.getAttribute('autocomplete'), required: p.required, prefix: p.parentElement.querySelector('.wm-phone__prefix').textContent.trim(),
              dir: p.parentElement.getAttribute('dir'), bound: p.dataset.waslaChangePhoneBound === 'true' }); })()
            """, ct);
        Assert.Equal("tel", attributes.GetProperty("type").GetString());
        Assert.Equal("numeric", attributes.GetProperty("inputmode").GetString());
        Assert.Equal("20", attributes.GetProperty("maxlength").GetString());
        Assert.Equal("tel-national", attributes.GetProperty("autocomplete").GetString());
        Assert.True(attributes.GetProperty("required").GetBoolean());
        Assert.Equal(("+90", "ltr"), (attributes.GetProperty("prefix").GetString(), attributes.GetProperty("dir").GetString()));
        Assert.True(attributes.GetProperty("bound").GetBoolean(), "The pilot phone field is not bound to the shared phone behaviour.");

        // 1. Typed digit by digit, grouped as it goes; the 11th digit is refused at the end and in the middle.
        await browser.EvaluateAsync<bool>($"(() => {{ {Phone}.focus(); return true; }})()", ct);
        foreach (var digit in "5324182790")
            await browser.InsertTextAsync(digit.ToString(), ct);
        Assert.Equal("532 418 27 90", await ValueAsync());
        await browser.InsertTextAsync("1", ct);
        Assert.Equal("532 418 27 90", await ValueAsync());
        await browser.EvaluateAsync<bool>($"(() => {{ {Phone}.setSelectionRange(4, 4); return true; }})()", ct);
        await browser.InsertTextAsync("9", ct);
        Assert.Equal("532 418 27 90", await ValueAsync());
        Assert.Equal(4, await CaretAsync());

        // 4. Letters and other characters never land; digits typed among them do.
        await browser.PressSelectAllAsync(ct);
        await browser.PressKeyAsync("Backspace", shift: false, ct);
        Assert.Equal(string.Empty, await ValueAsync());
        foreach (var key in new[] { "a", "5", "x", "-", "3", "(", "Ş", "2", "+", "." })
            await browser.InsertTextAsync(key, ct);
        Assert.Equal("532", await ValueAsync());

        // Editing keys work: Backspace, Home, Delete, arrows, and a selection replaced by typing.
        await browser.PressKeyAsync("Backspace", shift: false, ct);
        Assert.Equal("53", await ValueAsync());
        foreach (var digit in "24182790")
            await browser.InsertTextAsync(digit.ToString(), ct);
        Assert.Equal("532 418 27 90", await ValueAsync());
        await browser.PressKeyAsync("Home", shift: false, ct);
        await browser.PressKeyAsync("Delete", shift: false, ct);
        Assert.Equal("324 182 79 0", await ValueAsync());
        await browser.InsertTextAsync("5", ct);
        Assert.Equal("532 418 27 90", await ValueAsync());
        await browser.PressKeyAsync("End", shift: false, ct);
        await browser.PressKeyAsync("ArrowLeft", shift: false, ct);
        await browser.PressKeyAsync("Backspace", shift: false, ct);
        Assert.Equal("532 418 27 0", await ValueAsync());
        await browser.InsertTextAsync("9", ct);
        Assert.Equal("532 418 27 90", await ValueAsync());
        await browser.PressSelectAllAsync(ct);
        await browser.InsertTextAsync("2", ct);
        Assert.Equal("2", await ValueAsync());

        // 2. and 3. Pastes: cleaned, prefixes removed, at most the first 10 national digits.
        await PasteAsync("5324182790123456");
        Assert.Equal("532 418 27 90", await ValueAsync());
        foreach (var pasted in new[] { "+90 (532) 418-27-90", "0532 418 27 90", "0090 532 418 27 90", "+90 532 418 27 90 12", "(0532) 418-27-90" })
        {
            await PasteAsync(pasted);
            Assert.Equal("532 418 27 90", await ValueAsync());
        }

        // A number inserted as text in one go (an input method, a phone keyboard's clipboard suggestion) arrives whole.
        await browser.EvaluateAsync<bool>($"(() => {{ {Phone}.select(); return true; }})()", ct);
        await browser.InsertTextAsync("+90 (532) 418-27-90", ct);
        Assert.Equal("532 418 27 90", await ValueAsync());

        await PasteAsync("212 345 67 89");
        Assert.Equal("212 345 67 89", await ValueAsync());
        await PasteAsync("44", replaceAll: false);
        Assert.Equal("212 345 67 89", await ValueAsync());

        // 5. The form sends a valid 10-digit number and the server stores its digits.
        await PasteAsync("+90 (532) 418-27-90");
        await TypeAsync(browser, "pilot-restaurant-name", "Telefon Lokantası", ct);
        await browser.EvaluateAsync<bool>("(() => { const s = document.getElementById('pilot-city'); s.value = '06'; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()", ct);
        await TypeAsync(browser, "pilot-contact-name", "Can Telefoncu", ct);
        await TypeAsync(browser, "pilot-email", "can@telefon.test", ct);
        await SubmitAsync(browser, "document.querySelector('[data-wm-submit-label]').click()", ct);

        Assert.Equal("pilot-sent-title", await browser.EvaluateAsync<string>("document.activeElement.id", ct));
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        var stored = await db.ContactMessages.AsNoTracking().SingleAsync(m => m.RestaurantName == "Telefon Lokantası", ct);
        Assert.Equal(("06", "5324182790"), (stored.CityPlateCode, stored.Phone));
    }

    // The phone field's group spaces and text inserted in one go, with the steps the signup page runs too
    // (TurkishPhoneFieldScenario), on a desktop in Turkish and at phone width in Arabic right to left; the page does not
    // overflow sideways afterwards.
    [Theory]
    [InlineData("tr-TR", 1440, 900, false)]
    [InlineData("ar-SA", 390, 844, true)]
    public async Task ThePhoneField_StepsOverGroupSpaces_AndTakesTextInsertedInOneGoLikeAPaste(string culture, int width, int height, bool mobile)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, height, mobile, ct);
        await OpenAsync(browser, PublicSiteUrls.PilotPath, ct);

        await TurkishPhoneFieldScenario.RunAsync(browser, "pilot-phone", ct);

        await AssertNoSidewaysOverflowAsync(browser, ct);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static async Task<List<string>> TabOrderAsync(HeadlessChromium browser, int presses, CancellationToken ct)
    {
        await browser.EvaluateAsync<bool>("(() => { document.activeElement && document.activeElement.blur(); window.scrollTo(0, 0); return true; })()", ct);
        var order = new List<string>();
        for (var i = 0; i < presses; i++)
        {
            await browser.PressKeyAsync("Tab", shift: false, ct);
            order.Add(await browser.EvaluateAsync<string>("""
                (() => { const a = document.activeElement; if (!a) return ''; if (a.matches('[data-wm-submit-label]')) return 'submit';
                  if (a.matches('.wm-pilot-privacy__link')) return 'privacy-link'; return a.id || a.className || a.tagName; })()
                """, ct));
        }

        return order;
    }

    private static async Task TypeAsync(HeadlessChromium browser, string id, string text, CancellationToken ct)
    {
        await browser.EvaluateAsync<bool>($"(() => {{ const e = document.getElementById('{id}'); e.focus(); e.select && e.select(); return true; }})()", ct);
        await browser.InsertTextAsync(text, ct);
    }

    private static async Task SubmitAsync(HeadlessChromium browser, string action, CancellationToken ct)
    {
        await browser.EvaluateAsync<bool>($"(() => {{ window.__pilotOld = true; {action}; return true; }})()", ct);
        await browser.WaitForNavigationAsync("__pilotOld", ct);
    }

    private async Task OpenAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        await browser.NavigateAsync(new Uri($"http://{Host}:{_port}{path}"), ct);
        await WaitForAsync(browser, "document.readyState === 'complete' && !!document.querySelector('.wm-pilot') && document.fonts.status === 'loaded'", ct);
    }

    private static async Task AssertNoSidewaysOverflowAsync(HeadlessChromium browser, CancellationToken ct)
    {
        var result = await Json(browser, Overflow, ct);
        Assert.True(
            result.GetProperty("scroll").GetInt32() <= result.GetProperty("width").GetInt32() && result.GetProperty("out").GetArrayLength() == 0,
            $"Sideways overflow: {result}");
    }

    private static async Task<JsonElement> Json(HeadlessChromium browser, string expression, CancellationToken ct) =>
        JsonDocument.Parse(await browser.EvaluateAsync<string>(expression, ct)).RootElement.Clone();

    private static async Task WaitForAsync(HeadlessChromium browser, string expression, CancellationToken ct)
    {
        if (!await browser.WaitForAsync(expression, Wait, ct))
            Assert.Fail($"Timed out waiting for {expression} at {await browser.EvaluateAsync<string>("location.href", ct)}");
    }

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, int height, bool mobile, CancellationToken ct)
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the pilot application checks.");

        var browser = await HeadlessChromium.StartAsync(executable, ct);
        return await browser.SetUpAsync(async started =>
        {
            var cookie = Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)));
            await started.SetCookieAsync(new Uri($"http://{Host}:{_port}/"), CookieRequestCultureProvider.DefaultCookieName, cookie, ct);
            await started.SetViewportAsync(width, height, mobile, ct);
        });
    }

    private static string Text(string key, string culture) =>
        Resources.GetString(key, CultureInfo.GetCultureInfo(culture)) ?? throw new InvalidOperationException(key);
}
