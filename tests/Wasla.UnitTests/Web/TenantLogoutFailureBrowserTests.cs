using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Web;

/// <summary>
/// A tenant logout that cannot complete, and its recovery, in a real headless Chromium browser (WAS-186). The tenant
/// application is <see cref="TenantSessionWebHost"/> (real tenant resolution, cookie scheme with session revalidation,
/// controllers, views and the logout failure middleware); the browser maps the tenant host to the in-process server,
/// signs in through the rendered form and submits the layout's own logout form while the tenant database fails. The
/// failure page and the recovery (its "Try again", then the logout page's button) are then used with real keyboard and
/// mouse input. Skips only when no Chromium browser is installed (see <see cref="HeadlessChromium.BrowserVariable"/>) or
/// Bootstrap cannot be loaded from its CDN.
/// </summary>
[Trait("Category", "Browser")]
public sealed class TenantLogoutFailureBrowserTests : IAsyncLifetime
{
    private const string State = """
        (() => {
          const html = document.documentElement;
          const alert = document.querySelector('[data-logout-failed]');
          const control = document.querySelector('[data-logout-retry]') || document.querySelector('[data-logout-submit]');
          const r = control ? control.getBoundingClientRect() : null;
          const hit = r ? document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2) : null;
          const card = document.querySelector('.wasla-card');
          const heading = document.querySelector('h1');
          const rgb = c => (c.match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);
          const lum = c => { const [r, g, b] = rgb(c).map(v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
          let stored = null;
          try { stored = localStorage.getItem('Wasla.tenant.theme'); } catch { }
          return {
            path: location.pathname, dir: html.getAttribute('dir'), lang: html.getAttribute('lang'),
            theme: html.getAttribute('data-bs-theme'), stored,
            heading: heading ? heading.textContent.trim() : null,
            alert: alert ? alert.textContent.trim() : null,
            control: control ? control.textContent.trim() : null,
            controlKind: control ? (control.matches('[data-logout-retry]') ? 'retry' : 'submit') : null,
            controlHref: control ? control.getAttribute('href') : null,
            controlVisible: !!r && r.width > 0 && r.height > 0 && r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= innerHeight,
            controlHit: !!(control && hit && control.contains(hit)),
            controlCenter: r ? { x: r.left + r.width / 2, y: r.top + r.height / 2 } : null,
            forms: document.querySelectorAll('form').length,
            loginForm: !!document.querySelector('form[action="/auth/login"]'),
            cardLuminance: card ? lum(getComputedStyle(card).backgroundColor) : null,
            overflow: html.scrollWidth - html.clientWidth
          };
        })()
        """;

    private TenantSessionWebHost _host = null!;
    private string _ownerEmail = null!;

    public async ValueTask InitializeAsync()
    {
        // The redirect tests' host: its tenant databases render the Dashboard (the landing page) on SQLite.
        _host = await TenantPostLoginRedirectTests.StartHostAsync();
        _ownerEmail = _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner).Email;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private Uri Origin => new($"http://{TenantSessionWebHost.AlphaHost}:{_host.BaseAddress.Port}");

    /// <summary>
    /// The revocation cannot be stored. The failure page shows the localized alert and its "Try again" in both reading
    /// directions and at phone and desktop widths, stays light and leaves a stored dark choice alone, as the sign-in
    /// pages do. With storage working again, keyboard only: Tab to "Try again", Enter opens the logout page, Tab to its
    /// button, Enter logs out. The session is then revoked and the browser is signed out.
    /// </summary>
    [Theory]
    [InlineData("tr-TR", 1280, false, "Çıkış yap", "Çıkış tamamlanamadı. Oturumunuz açık kalmış olabilir. Lütfen tekrar deneyin.", "Tekrar dene")]
    [InlineData("ar-SA", 390, true, "تسجيل الخروج", "تعذر إكمال تسجيل الخروج. قد تظل جلستك مفتوحة. يرجى المحاولة مرة أخرى.", "إعادة المحاولة")]
    [InlineData("en-US", 390, true, "Logout", "Logout could not be completed. Your session may still be open. Please try again.", "Try again")]
    [InlineData("ru-RU", 1280, false, "Выход из системы", "Не удалось завершить выход. Ваш сеанс может оставаться открытым. Повторите попытку.", "Повторить")]
    public async Task FailedRevocation_ShowsTheLocalizedFailurePage_AndTheKeyboardRetryLogsOut(
        string culture, int width, bool mobile, string heading, string message, string tryAgain)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync(culture, width, mobile ? 844 : 860, mobile, "dark", ct);
        await SignInAsync(browser, ct);
        await browser.EvaluateAsync<bool>("(() => { localStorage.setItem('Wasla.tenant.theme', 'dark'); return true; })()", ct);
        FailAlpha(new FailingCommands(IsRevocation));

        await SubmitLayoutLogoutAsync(browser, ct);
        var state = await StateAsync(browser, ct);

        Assert.Equal("/auth/logout", state.GetProperty("path").GetString());
        Assert.Equal(culture, state.GetProperty("lang").GetString());
        Assert.Equal(culture == "ar-SA" ? "rtl" : "ltr", state.GetProperty("dir").GetString());
        Assert.Equal(heading, state.GetProperty("heading").GetString());
        Assert.Equal(message, state.GetProperty("alert").GetString());
        Assert.Equal("retry", state.GetProperty("controlKind").GetString());
        Assert.Equal(tryAgain, state.GetProperty("control").GetString());
        Assert.Equal("/auth/logout", state.GetProperty("controlHref").GetString());
        Assert.True(state.GetProperty("controlVisible").GetBoolean(), state.ToString());
        Assert.True(state.GetProperty("controlHit").GetBoolean(), state.ToString());
        Assert.Equal(0, state.GetProperty("forms").GetInt32());
        Assert.True(state.GetProperty("overflow").GetDouble() <= 1, state.ToString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("theme").ValueKind);
        Assert.Equal("dark", state.GetProperty("stored").GetString());
        Assert.True(state.GetProperty("cardLuminance").GetDouble() > 0.6, state.ToString());
        var alert = Assert.Single(await browser.AccessibleNodesAsync("[data-logout-failed]", ct));
        Assert.Equal("alert", alert.Role);
        var retry = Assert.Single(await browser.AccessibleNodesAsync("[data-logout-retry]", ct));
        Assert.Equal(("link", tryAgain), (retry.Role, retry.Name));
        Assert.Null(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);

        UsePlainAlpha();
        await TabToAsync(browser, "[data-logout-retry]", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        await WaitForAsync(browser, "location.pathname === '/auth/logout' && !!document.querySelector('[data-logout-submit]')", "the logout page", ct);
        var logoutPage = await StateAsync(browser, ct);

        Assert.Equal("submit", logoutPage.GetProperty("controlKind").GetString());
        Assert.Equal(heading, logoutPage.GetProperty("control").GetString());
        Assert.True(logoutPage.GetProperty("controlVisible").GetBoolean(), logoutPage.ToString());
        Assert.True(logoutPage.GetProperty("overflow").GetDouble() <= 1, logoutPage.ToString());
        Assert.Equal(JsonValueKind.Null, logoutPage.GetProperty("alert").ValueKind);
        Assert.Equal(culture == "ar-SA" ? "rtl" : "ltr", logoutPage.GetProperty("dir").GetString());

        await TabToAsync(browser, "[data-logout-submit]", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
        await WaitForPageAsync(browser, "/auth/login", ct);

        Assert.True((await StateAsync(browser, ct)).GetProperty("loginForm").GetBoolean());
        Assert.NotNull(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);
        await GoAsync(browser, "/dashboard", "/auth/login", ct);
    }

    /// <summary>
    /// The revocation was stored, but the database reported a failure afterwards. The page claims neither outcome; a real
    /// click on "Try again" finds the session already ended, and the browser follows the redirects to the login page and
    /// stays there.
    /// </summary>
    [Fact]
    public async Task RevocationStoredButReportedAsFailed_TryAgainClick_EndsOnTheLoginPage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("ar-SA", 390, 844, mobile: true, "light", ct);
        await SignInAsync(browser, ct);
        FailAlpha(new FailingAfterExecution(IsRevocation));

        await SubmitLayoutLogoutAsync(browser, ct);
        var state = await StateAsync(browser, ct);
        Assert.Equal("تعذر إكمال تسجيل الخروج. قد تظل جلستك مفتوحة. يرجى المحاولة مرة أخرى.", state.GetProperty("alert").GetString());
        Assert.NotNull(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);

        var center = state.GetProperty("controlCenter");
        await browser.ClickAtAsync(center.GetProperty("x").GetDouble(), center.GetProperty("y").GetDouble(), ct);
        await WaitForPageAsync(browser, "/auth/login", ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        var login = await StateAsync(browser, ct);
        Assert.Equal("/auth/login", login.GetProperty("path").GetString());
        Assert.True(login.GetProperty("loginForm").GetBoolean(), login.ToString());
        Assert.Equal("rtl", login.GetProperty("dir").GetString());
    }

    /// <summary>
    /// The tenant database cannot be opened at all, so the session cannot be checked before the logout action. The
    /// failure page still renders with its styles; a click on "Try again" while the outage lasts gets the same page again,
    /// not a redirect. Once the database is back, "Try again" opens the logout page and a click on its button logs out.
    /// </summary>
    [Fact]
    public async Task TenantDatabaseUnreachable_FailurePageRenders_AndTryAgainLogsOutOnceItIsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await BrowserAsync("en-US", 1280, 860, mobile: false, "dark", ct);
        await SignInAsync(browser, ct);
        _host.Tenants.Override(_host.AlphaId, _ => Task.FromException<TenantDbContext>(new SimulatedDbException()));

        await SubmitLayoutLogoutAsync(browser, ct);
        var state = await StateAsync(browser, ct);
        Assert.Equal("Logout could not be completed. Your session may still be open. Please try again.", state.GetProperty("alert").GetString());
        Assert.True(state.GetProperty("cardLuminance").GetDouble() > 0.6, state.ToString());

        await ClickControlAsync(browser, state, ct);
        await WaitForAsync(browser, "!window.__waslaPreviousPage && !!document.querySelector('[data-logout-failed]')", "the failure page again", ct);
        state = await StateAsync(browser, ct);
        Assert.Equal("/auth/logout", state.GetProperty("path").GetString());
        Assert.Equal("retry", state.GetProperty("controlKind").GetString());

        UsePlainAlpha();
        await ClickControlAsync(browser, state, ct);
        await WaitForAsync(browser, "location.pathname === '/auth/logout' && !!document.querySelector('[data-logout-submit]')", "the logout page", ct);
        await ClickControlAsync(browser, await StateAsync(browser, ct), ct);
        await WaitForPageAsync(browser, "/auth/login", ct);

        Assert.NotNull(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);
    }

    private static bool IsRevocation(DbCommand command) =>
        command.CommandText.Contains("UPDATE \"TenantSessions\"", StringComparison.Ordinal);

    private void FailAlpha(IInterceptor interceptor) =>
        _host.Tenants.Override(_host.AlphaId, _ => Task.FromResult(new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlite(_host.Tenants.ConnectionStringFor(_host.AlphaId))
                .AddInterceptors(interceptor)
                .Options)));

    /// <summary>A working tenant database again; enough for the session check and the revocation (no Dashboard after this).</summary>
    private void UsePlainAlpha() =>
        _host.Tenants.Override(_host.AlphaId, _ => Task.FromResult(new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(_host.Tenants.ConnectionStringFor(_host.AlphaId)).Options)));

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, int height, bool mobile, string theme, CancellationToken ct)
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser logout checks.");

        // The tenant host name resolves to the in-process server; everything else (the CDN) resolves normally.
        var browser = await HeadlessChromium.StartAsync(
            executable,
            [$"--host-resolver-rules=MAP {TenantSessionWebHost.AlphaHost} 127.0.0.1"],
            ct);
        return await browser.SetUpAsync(async started =>
        {
            await started.SetCookieAsync(Origin, CookieRequestCultureProvider.DefaultCookieName,
                Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))), ct);
            await started.SetViewportAsync(width, height, mobile, ct);
            await started.SetSystemColorSchemeAsync(theme, ct);
        });
    }

    private async Task SignInAsync(HeadlessChromium browser, CancellationToken ct)
    {
        await GoAsync(browser, "/auth/login", "/auth/login", ct);
        await browser.EvaluateAsync<bool>($$"""
            (() => {
              const form = document.querySelector('form[action="/auth/login"]');
              form.querySelector('[name="Email"]').value = {{JsonSerializer.Serialize(_ownerEmail)}};
              form.querySelector('[name="Password"]').value = {{JsonSerializer.Serialize(TenantSessionWebHost.Password)}};
              form.querySelector('button[type="submit"]').click();
              return true;
            })()
            """, ct);
        await WaitForPageAsync(browser, "/dashboard", ct);
        await WaitForAsync(browser, "!!document.querySelector('form[action=\"/auth/logout\"] button[type=\"submit\"]')", "the layout's logout form", ct);
    }

    /// <summary>Submits the tenant layout's own logout form (with the token the page was rendered with) and waits for the answer.</summary>
    private static async Task SubmitLayoutLogoutAsync(HeadlessChromium browser, CancellationToken ct)
    {
        await browser.EvaluateAsync<bool>("""
            (() => {
              window.__waslaPreviousPage = true;
              document.querySelector('form[action="/auth/logout"] button[type="submit"]').click();
              return true;
            })()
            """, ct);
        await WaitForAsync(browser, "!window.__waslaPreviousPage && !!document.querySelector('[data-logout-failed]')", "the logout failure page", ct);
        await WaitForPageAsync(browser, "/auth/logout", ct);
    }

    /// <summary>A real mouse click on the page's control; the current document is marked, so a wait can tell the next one apart.</summary>
    private static async Task ClickControlAsync(HeadlessChromium browser, JsonElement state, CancellationToken ct)
    {
        Assert.True(state.GetProperty("controlHit").GetBoolean(), state.ToString());
        await browser.EvaluateAsync<bool>("(() => { window.__waslaPreviousPage = true; return true; })()", ct);
        var center = state.GetProperty("controlCenter");
        await browser.ClickAtAsync(center.GetProperty("x").GetDouble(), center.GetProperty("y").GetDouble(), ct);
    }

    /// <summary>Real Tab presses from the top of the page until the control has keyboard focus, shown with its focus ring.</summary>
    private static async Task TabToAsync(HeadlessChromium browser, string selector, CancellationToken ct)
    {
        var focused = $"!!document.activeElement && document.activeElement.matches({JsonSerializer.Serialize(selector)})";
        for (var press = 0; press < 8; press++)
        {
            await browser.PressKeyAsync("Tab", shift: false, ct);
            if (await browser.EvaluateAsync<bool>(focused, ct))
            {
                Assert.True(await browser.EvaluateAsync<bool>("document.activeElement.matches(':focus-visible')", ct),
                    $"{selector} has focus without a visible focus indicator.");
                return;
            }
        }

        Assert.Fail($"Tab did not reach {selector}.");
    }

    private async Task GoAsync(HeadlessChromium browser, string path, string expectedPath, CancellationToken ct)
    {
        await browser.EvaluateAsync<bool>($"(() => {{ location.href = {JsonSerializer.Serialize(new Uri(Origin, path).ToString())}; return true; }})()", ct);
        await WaitForPageAsync(browser, expectedPath, ct);
    }

    private static async Task WaitForAsync(HeadlessChromium browser, string condition, string what, CancellationToken ct)
    {
        if (!await browser.WaitForAsync($"({condition}) && document.readyState === 'complete'", TimeSpan.FromSeconds(30), ct))
            Assert.Fail($"Expected {what}, the browser is at {await browser.EvaluateAsync<string>("location.pathname", ct)}.");
    }

    private static async Task WaitForPageAsync(HeadlessChromium browser, string expectedPath, CancellationToken ct)
    {
        await WaitForAsync(browser, $"location.pathname === {JsonSerializer.Serialize(expectedPath)}", expectedPath, ct);
        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant pages cannot be exercised.");
        await Task.Delay(150, ct);
    }

    private static Task<JsonElement> StateAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>(State, ct);

    private sealed class SimulatedDbException() : DbException("Simulated tenant database outage (test).");

    /// <summary>Fails every command <paramref name="match"/> accepts before it reaches the database.</summary>
    private sealed class FailingCommands(Func<DbCommand, bool> match) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            match(command) ? throw new SimulatedDbException() : ValueTask.FromResult(result);
    }

    /// <summary>Lets every command <paramref name="match"/> accepts run and commit, then fails it.</summary>
    private sealed class FailingAfterExecution(Func<DbCommand, bool> match) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            match(command) ? throw new SimulatedDbException() : ValueTask.FromResult(result);
    }
}
