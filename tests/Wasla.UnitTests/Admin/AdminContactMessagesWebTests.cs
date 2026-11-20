using System.Net;
using System.Xml.Linq;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// The Central Admin contact messages pages over real HTTP (<see cref="AdminWebHost"/>): only a Central Admin reads them,
/// the list shows who wrote about what without the sender's address or message, the detail shows everything with the
/// sender's text encoded, and pages are newest first. Fake data only.
/// </summary>
public sealed class AdminContactMessagesWebTests : IAsyncLifetime
{
    private readonly CentralTestDatabase _central = new();
    private readonly RecordingTenantDbFactory _tenants = new();
    private AdminWebHost _host = null!;
    private ContactMessage _message = null!;

    public async ValueTask InitializeAsync()
    {
        using (var db = _central.CreateContext())
        {
            _message = ContactMessage.Create(
                "<script>alert('name')</script> Ayşe",
                "Lezzet Ocakbaşı",
                "ayse@lezzet.test",
                "5446695691",
                ContactTopic.PrintBridgeSetup,
                "First line\n<img src=x onerror=alert(1)>\nThird line",
                "tr-TR",
                true,
                new DateTime(2026, 10, 27, 9, 15, 0, DateTimeKind.Utc));
            _message.MarkNotificationFailed(new DateTime(2026, 10, 27, 9, 15, 2, DateTimeKind.Utc));
            db.ContactMessages.Add(_message);
            for (var i = 0; i < 52; i++)
            {
                db.ContactMessages.Add(ContactMessage.Create(
                    $"Older sender {i:00}", "Older restaurant", $"older{i}@example.test", null, ContactTopic.Other, "Hello", "en-US", true,
                    new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc).AddMinutes(i)));
            }

            db.SaveChanges();
        }

        _host = await AdminWebHost.StartAsync(_central, _tenants);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _tenants.Dispose();
        _central.Dispose();
    }

    [Fact]
    public async Task List_ShowsNewestFirst_WithoutTheSendersAddressOrMessage()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");

        var page = await browser.GetAsync("/admin/contact-messages");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        var html = WebUtility.HtmlDecode(page.Body);
        Assert.Contains(Resource("Admin.ContactMessages.Title"), html, StringComparison.Ordinal);
        Assert.Contains("Lezzet Ocakbaşı", html, StringComparison.Ordinal);
        Assert.Contains(Resource("Contact.Topic.PrintBridgeSetup"), html, StringComparison.Ordinal);
        Assert.Contains(Resource("Admin.ContactMessages.Status.Failed"), html, StringComparison.Ordinal);
        Assert.Contains("2026-10-27 09:15 UTC", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("Lezzet Ocakbaşı", StringComparison.Ordinal) < html.IndexOf("Older sender 51", StringComparison.Ordinal));
        Assert.DoesNotContain("ayse@lezzet.test", html, StringComparison.Ordinal);
        Assert.DoesNotContain("First line", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", page.Body, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/contact-messages?page=2\"", page.Body);
        Assert.DoesNotContain("Older sender 01", html, StringComparison.Ordinal);
        // The sidebar entry is active on this page.
        Assert.Contains("class=\"nav-link active\" href=\"/admin/contact-messages\"", page.Body);

        var second = WebUtility.HtmlDecode((await browser.GetAsync("/admin/contact-messages?page=2")).Body);
        Assert.Contains("Older sender 01", second, StringComparison.Ordinal);
        Assert.Contains("Older sender 00", second, StringComparison.Ordinal);
        Assert.DoesNotContain("Lezzet Ocakbaşı", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_ShowsTheWholeMessage_WithTheSendersTextEncoded()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");

        var page = await browser.GetAsync($"/admin/contact-messages/{_message.Id}");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.DoesNotContain("<script>alert", page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", page.Body, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", page.Body, StringComparison.Ordinal);
        var html = WebUtility.HtmlDecode(page.Body);
        Assert.Contains("href=\"mailto:ayse@lezzet.test\"", page.Body);
        Assert.Contains("+90 544 669 56 91", html, StringComparison.Ordinal);
        Assert.Contains("First line\n<img src=x onerror=alert(1)>\nThird line", html, StringComparison.Ordinal);
        Assert.Contains(Resource("Admin.ContactMessages.Status.Failed"), html, StringComparison.Ordinal);
        Assert.Contains("2026-10-27 09:15 UTC", html, StringComparison.Ordinal);
        Assert.Contains("<code>tr-TR</code>", page.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PilotApplications_HaveTheirOwnFilter_AndTheirDetailNamesTheCityAndNote()
    {
        var pilotId = Guid.NewGuid();
        using (var db = _central.CreateContext())
        {
            db.Cities.Add(new City { Id = 34, CountryCode = "TR", Name = "İstanbul", PlateCode = "34", IsActive = true });
            var pilot = ContactMessage.CreatePilotApplication(pilotId, "Pilot Kebap", "Elif Aydın", "elif@pilot.test", "5324182790", "34", null, "tr-TR",
                new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc));
            pilot.MarkNotificationFailed(new DateTime(2026, 11, 20, 9, 0, 1, DateTimeKind.Utc));
            db.ContactMessages.Add(pilot);
            db.SaveChanges();
        }

        using var browser = await _host.SignedInAdminAsync("en-US");

        var pilots = WebUtility.HtmlDecode((await browser.GetAsync("/admin/contact-messages?kind=pilot")).Body);
        Assert.Contains("Pilot Kebap", pilots, StringComparison.Ordinal);
        Assert.Contains(Resource("Contact.Topic.PilotApplication"), pilots, StringComparison.Ordinal);
        Assert.DoesNotContain("Lezzet Ocakbaşı", pilots, StringComparison.Ordinal);
        Assert.Contains("data-contact-filter=\"pilot\"", pilots, StringComparison.Ordinal);

        var contacts = WebUtility.HtmlDecode((await browser.GetAsync("/admin/contact-messages?kind=contact")).Body);
        Assert.DoesNotContain("Pilot Kebap", contacts, StringComparison.Ordinal);
        Assert.Contains("Lezzet Ocakbaşı", contacts, StringComparison.Ordinal);

        var detail = await browser.GetAsync($"/admin/contact-messages/{pilotId}");
        var html = WebUtility.HtmlDecode(detail.Body);
        Assert.Contains("İstanbul", html, StringComparison.Ordinal);
        Assert.Contains("data-contact-city=\"34\"", detail.Body);
        Assert.Contains(Resource("Admin.ContactMessages.NoNote"), html, StringComparison.Ordinal);
        Assert.Contains("data-contact-resend", detail.Body);
    }

    [Fact]
    public async Task Resend_NeedsTheAntiforgeryToken_AndWithoutSmtpSaysNothingWasSent()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var page = await browser.GetAsync($"/admin/contact-messages/{_message.Id}");
        Assert.Contains("data-contact-resend", page.Body);

        var refused = await browser.PostAsync($"/admin/contact-messages/{_message.Id}/resend-notification", antiforgeryToken: null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);

        var posted = await browser.PostAsync($"/admin/contact-messages/{_message.Id}/resend-notification", AdminBrowser.AntiforgeryTokenIn(page.Body));
        Assert.Equal(HttpStatusCode.Redirect, posted.Status);

        var after = await browser.GetAsync($"/admin/contact-messages/{_message.Id}");
        var html = WebUtility.HtmlDecode(after.Body);
        // Email:Provider is Log here: the page never claims delivery.
        Assert.Contains(Resource("Admin.ContactMessages.ResendNotConfigured"), html, StringComparison.Ordinal);
        Assert.Contains(Resource("Admin.ContactMessages.Status.NotConfigured"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(Resource("Admin.ContactMessages.ResendSent"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownMessage_IsNotFound()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");

        var page = await browser.GetAsync($"/admin/contact-messages/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, page.Status);
    }

    [Fact]
    public async Task AnonymousAndTenantSessions_CannotReadMessages()
    {
        using var anonymous = _host.NewBrowser();
        using var tenantUser = _host.NewBrowser();
        await tenantUser.SignInTenantUserAsync(Guid.NewGuid());

        foreach (var browser in new[] { anonymous, tenantUser })
        {
            foreach (var path in new[] { "/admin/contact-messages", $"/admin/contact-messages/{_message.Id}" })
            {
                var page = await browser.GetAsync(path);
                Assert.Equal(HttpStatusCode.Redirect, page.Status);
                Assert.Contains("/admin/login", page.Location!.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("Lezzet", page.Body, StringComparison.Ordinal);
            }
        }
    }

    private static string Resource(string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", "SharedResource.en-US.resx"))
            .Root!
            .Elements("data")
            .Single(e => e.Attribute("name")!.Value == key)
            .Element("value")!.Value;
}
