using System.Text.Json;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// Renders the real Admin pages in a headless Chromium browser (Edge or Chrome) and measures them. AdminLTE's live
/// region used <c>left: -10000px</c>, which in a right-to-left document produced about 10,000px of page-level
/// horizontal overflow; on phones the RTL sidebar also covered the content. Skips only when no Chromium browser is
/// installed (set <see cref="HeadlessChromium.BrowserVariable"/> to point at one) or AdminLTE cannot be loaded.
/// </summary>
public sealed class AdminRtlLayoutBrowserTests : IAsyncLifetime
{
    private static readonly (int Width, int Height, bool Mobile)[] Viewports = [(375, 812, true), (1366, 860, false)];

    private const string Metrics = """
        (() => {
          const root = document.documentElement;
          const live = document.getElementById('live-region');
          const style = live && getComputedStyle(live);
          const rect = live && live.getBoundingClientRect();
          const sidebar = document.querySelector('.app-sidebar').getBoundingClientRect();
          return {
            dir: root.dir,
            innerWidth: window.innerWidth,
            clientWidth: root.clientWidth,
            scrollWidth: root.scrollWidth,
            liveRole: live && live.getAttribute('role'),
            liveAriaLive: live && live.getAttribute('aria-live'),
            liveDisplay: style && style.display,
            liveVisibility: style && style.visibility,
            liveClipPath: style && style.clipPath,
            liveLeft: rect && rect.left,
            liveRight: rect && rect.right,
            sidebarLeft: sidebar.left,
            sidebarRight: sidebar.right
          };
        })()
        """;

    private readonly CentralTestDatabase _central = new();
    private readonly RecordingTenantDbFactory _tenants = new();
    private AdminWebHost _host = null!;
    private Guid _tenantId;

    public async ValueTask InitializeAsync()
    {
        var tenant = _central.AddTenant("rtl-kebap", name: "RTL Kebap", planCode: "Pro");
        _tenantId = tenant.Id;
        _central.AddDevice(tenant.Id, "Kitchen", true, DateTime.UtcNow);
        _central.AddRegistration("rtl-waiting", Wasla.Domain.Enums.PendingRegistrationStatus.PaymentSucceeded, paymentSucceededAt: DateTime.UtcNow.AddHours(-2));
        _tenants.CreateTenantDatabase(tenant.Id);
        _tenants.Seed(tenant.Id, db => TenantSeed.HealthyTenant(db, DateTime.UtcNow));
        _host = await AdminWebHost.StartAsync(_central, _tenants);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _tenants.Dispose();
        _central.Dispose();
    }

    [Theory]
    [InlineData("ar-SA", "rtl")]
    [InlineData("en-US", "ltr")]
    public async Task AdminPages_HaveNoPageLevelHorizontalOverflow_AndKeepAnAccessibleLiveRegion(string culture, string direction)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await SignedInBrowserAsync(culture, ct);

        foreach (var (width, height, mobile) in Viewports)
        {
            await browser.SetViewportAsync(width, height, mobile, ct);
            foreach (var path in new[]
                     {
                         "/admin",
                         "/admin/customers?sort=name&dir=asc",
                         $"/admin/customers/{_tenantId}",
                         "/admin/pending-registrations"
                     })
            {
                var page = $"{path} at {width}px ({culture})";
                var m = await LoadAsync(browser, path, ct);

                Assert.Equal(direction, m.GetProperty("dir").GetString());
                Assert.Equal(width, m.GetProperty("innerWidth").GetInt32());
                Assert.True(m.GetProperty("scrollWidth").GetInt32() <= m.GetProperty("clientWidth").GetInt32() + 1,
                    $"{page}: document {m.GetProperty("scrollWidth")}px wide in a {m.GetProperty("clientWidth")}px viewport");

                // The live region stays exposed to assistive technology, clipped in place inside the viewport.
                Assert.Equal("status", m.GetProperty("liveRole").GetString());
                Assert.Equal("polite", m.GetProperty("liveAriaLive").GetString());
                Assert.NotEqual("none", m.GetProperty("liveDisplay").GetString());
                Assert.Equal("visible", m.GetProperty("liveVisibility").GetString());
                Assert.Equal("inset(50%)", m.GetProperty("liveClipPath").GetString());
                Assert.InRange(m.GetProperty("liveLeft").GetDouble(), -1, width + 1);
                Assert.InRange(m.GetProperty("liveRight").GetDouble(), -1, width + 1);

                if (mobile)
                {
                    // Off-canvas: the sidebar lies entirely outside the viewport on the inline-start side.
                    var offCanvas = direction == "rtl"
                        ? m.GetProperty("sidebarLeft").GetDouble() >= width - 1
                        : m.GetProperty("sidebarRight").GetDouble() <= 1;
                    Assert.True(offCanvas, $"{page}: sidebar {m.GetProperty("sidebarLeft")}..{m.GetProperty("sidebarRight")} covers the content");
                }
            }
        }
    }

    [Fact]
    public async Task LiveRegion_StillAnnouncesAdminLteStatusMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await SignedInBrowserAsync("ar-SA", ct);
        await browser.SetViewportAsync(375, 812, mobile: true, ct);
        await LoadAsync(browser, "/admin", ct);

        await browser.EvaluateAsync<bool>(
            "(() => { const a = document.createElement('div'); a.className = 'alert alert-success'; a.textContent = 'Saved for test'; document.querySelector('main').appendChild(a); return true; })()",
            ct);

        Assert.True(await browser.WaitForAsync("document.getElementById('live-region').textContent === 'Saved for test'", TimeSpan.FromSeconds(5), ct));
        Assert.True(await browser.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1", ct));
    }

    [Fact]
    public async Task WithoutTheLayoutCorrection_ArabicPagesOverflow_SoTheCheckIsMeaningful()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await SignedInBrowserAsync("ar-SA", ct);
        await browser.SetViewportAsync(375, 812, mobile: true, ct);
        await LoadAsync(browser, "/admin", ct);

        var overflow = await browser.EvaluateAsync<int>(
            "(async () => { document.querySelector('link[href*=\"wasla-admin-layout.css\"]').disabled = true; await new Promise(r => setTimeout(r, 200)); return document.documentElement.scrollWidth; })()",
            ct);

        Assert.True(overflow > 5000, $"Expected the uncorrected AdminLTE live region to overflow; document was {overflow}px wide.");
    }

    private async Task<HeadlessChromium> SignedInBrowserAsync(string culture, CancellationToken ct)
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser layout checks.");

        using var session = await _host.SignedInAdminAsync(culture);
        var browser = await HeadlessChromium.StartAsync(executable, ct);
        foreach (var cookie in session.Cookies)
            await browser.SetCookieAsync(_host.BaseAddress, cookie.Name, cookie.Value, ct);
        return browser;
    }

    /// <summary>Loads a page, waits for AdminLTE to create its live region, and returns the layout metrics.</summary>
    private async Task<JsonElement> LoadAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        if (!await browser.WaitForAsync("!!document.getElementById('live-region')", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("AdminLTE did not load (its CDN is unreachable), so the live-region layout cannot be measured.");

        return await browser.EvaluateAsync<JsonElement>(Metrics, ct);
    }
}
