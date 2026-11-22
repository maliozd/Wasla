using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Contact;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// The Central Admin pilot application pages of WAS-222 over real HTTP (<see cref="AdminWebHost"/>): the team's status
/// and recorded contacts, and the data subject request procedure (find, download, correct, delete). Only a Central
/// Admin reaches them, every change needs the antiforgery token, the search is posted so no address lands in a URL, the
/// download is JSON that is not cached, and a deletion needs the identity confirmation and leaves a destruction record
/// naming the administrator. Fictional data only.
/// </summary>
public sealed class AdminPilotApplicationRequestsWebTests : IAsyncLifetime
{
    private readonly CentralTestDatabase _central = new();
    private readonly RecordingTenantDbFactory _tenants = new();
    private AdminWebHost _host = null!;
    private Guid _pilot;
    private Guid _contact;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = _central.CreateContext())
        {
            db.Cities.AddRange(PilotProvinceSeed.All(idOffset: 1000));
            await db.SaveChangesAsync(Ct);
            _pilot = await PilotRetentionTestData.AddPilotAsync(db, DateTime.UtcNow.AddDays(-20), PilotApplicationStatus.New, ct: Ct);
            _contact = await PilotRetentionTestData.AddContactMessageAsync(db, DateTime.UtcNow.AddDays(-20), Ct);
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
    public async Task ThePilotDetail_ShowsTheStatusTheDeletionDateAndTheRequestActions_AContactMessageDoesNot()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");

        var pilot = await browser.GetAsync($"/admin/contact-messages/{_pilot}");
        var html = WebUtility.HtmlDecode(pilot.Body);
        Assert.Equal(HttpStatusCode.OK, pilot.Status);
        Assert.Contains("data-pilot-status=\"New\"", pilot.Body, StringComparison.Ordinal);
        Assert.Contains(Resource("Admin.Pilot.Status.New"), html, StringComparison.Ordinal);
        Assert.Contains(string.Format(Resource("Admin.Pilot.RetentionHelp"), 90), html, StringComparison.Ordinal);
        Assert.Contains("data-pilot-delete-on", pilot.Body, StringComparison.Ordinal);
        foreach (var marker in new[] { "data-pilot-status-form", "data-pilot-record-contact", "data-pilot-export", "data-pilot-correct", "data-pilot-delete" })
            Assert.Contains(marker, pilot.Body, StringComparison.Ordinal);
        // Every control has a label it is named by.
        Assert.Contains("<label class=\"form-label small mb-1\" for=\"pilot-status\">", pilot.Body, StringComparison.Ordinal);
        Assert.Contains("for=\"pilot-identity-verified\"", pilot.Body, StringComparison.Ordinal);

        var contact = await browser.GetAsync($"/admin/contact-messages/{_contact}");
        Assert.DoesNotContain("data-pilot-status-card", contact.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("data-pilot-data-request", contact.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheArabicDetail_IsRightToLeft_InItsOwnLanguage()
    {
        using var browser = await _host.SignedInAdminAsync("ar-SA");

        var page = await browser.GetAsync($"/admin/contact-messages/{_pilot}");
        var html = WebUtility.HtmlDecode(page.Body);

        Assert.Contains("dir=\"rtl\"", page.Body, StringComparison.Ordinal);
        Assert.Contains(Resource("Admin.Pilot.DataRequestTitle", "ar-SA"), html, StringComparison.Ordinal);
        Assert.Contains(Resource("Admin.Pilot.IdentityVerified", "ar-SA"), html, StringComparison.Ordinal);
        Assert.Contains(string.Format(Resource("Admin.Pilot.RecordContactHelp", "ar-SA"), 90), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAndRecordedContact_MoveTheLastActivity_AndNeedTheAntiforgeryToken()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var page = await browser.GetAsync($"/admin/contact-messages/{_pilot}");
        var token = AdminBrowser.AntiforgeryTokenIn(page.Body);
        var before = (await RowAsync(_pilot))!.PilotLastActivityAtUtc!.Value;

        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostFormAsync($"/admin/contact-messages/{_pilot}/status", Fields(("status", "Accepted")), null)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync($"/admin/contact-messages/{_pilot}/record-contact", null)).Status);
        Assert.Equal(before, (await RowAsync(_pilot))!.PilotLastActivityAtUtc);

        var set = await browser.PostFormAsync($"/admin/contact-messages/{_pilot}/status", Fields(("status", "Accepted")), token);
        Assert.Equal(HttpStatusCode.Redirect, set.Status);
        var row = (await RowAsync(_pilot))!;
        Assert.Equal(PilotApplicationStatus.Accepted, row.PilotStatus);
        Assert.True(row.PilotLastActivityAtUtc > before);
        Assert.Contains(Resource("Admin.Pilot.StatusSaved"), WebUtility.HtmlDecode((await browser.GetAsync($"/admin/contact-messages/{_pilot}")).Body), StringComparison.Ordinal);

        foreach (var refused in new[] { "New", "0", "2", "Unknown" })
        {
            await browser.PostFormAsync($"/admin/contact-messages/{_pilot}/status", Fields(("status", refused)), token);
            Assert.Equal(PilotApplicationStatus.Accepted, (await RowAsync(_pilot))!.PilotStatus);
        }

        var recorded = await browser.PostAsync($"/admin/contact-messages/{_pilot}/record-contact", token);
        Assert.Equal(HttpStatusCode.Redirect, recorded.Status);
        Assert.True((await RowAsync(_pilot))!.PilotLastActivityAtUtc >= row.PilotLastActivityAtUtc);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync($"/admin/contact-messages/{_contact}/record-contact", token)).Status);
        Assert.Null((await RowAsync(_contact))!.PilotLastActivityAtUtc);
    }

    [Fact]
    public async Task Find_IsPosted_AndListsThePersonsApplicationByExactEmailOrPhone()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var form = await browser.GetAsync("/admin/contact-messages/find");
        Assert.Equal(HttpStatusCode.OK, form.Status);
        Assert.Contains("method=\"post\" action=\"/admin/contact-messages/find\"", form.Body, StringComparison.Ordinal);
        var token = AdminBrowser.AntiforgeryTokenIn(form.Body);
        var email = PilotRetentionTestData.Email(_pilot);

        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostFormAsync("/admin/contact-messages/find", Fields(("Email", email)), null)).Status);

        var byEmail = await browser.PostFormAsync("/admin/contact-messages/find", Fields(("Email", email.ToUpperInvariant())), token);
        Assert.Equal(HttpStatusCode.OK, byEmail.Status);
        Assert.Contains("data-pilot-find-results=\"1\"", byEmail.Body, StringComparison.Ordinal);
        Assert.Contains($"href=\"/admin/contact-messages/{_pilot}\"", byEmail.Body, StringComparison.Ordinal);

        var byPhone = await browser.PostFormAsync("/admin/contact-messages/find", Fields(("Phone", "0532 123 45 67")), token);
        Assert.Contains("data-pilot-find-results=\"1\"", byPhone.Body, StringComparison.Ordinal);

        var none = await browser.PostFormAsync("/admin/contact-messages/find", Fields(("Email", "old.contact@retention.test")), token);
        Assert.Contains("data-pilot-find-results=\"0\"", none.Body, StringComparison.Ordinal);

        var empty = await browser.PostFormAsync("/admin/contact-messages/find", Fields(("Email", " ")), token);
        Assert.Contains(Resource("Admin.Pilot.FindNeedsValue"), WebUtility.HtmlDecode(empty.Body), StringComparison.Ordinal);

        var badPhone = await browser.PostFormAsync("/admin/contact-messages/find", Fields(("Phone", "12")), token);
        Assert.Contains(Resource("Validation.ContactPhoneInvalid"), WebUtility.HtmlDecode(badPhone.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_IsAnUncachedJsonAttachment_WithEverythingStored_AndNeedsTheToken()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var page = await browser.GetAsync($"/admin/contact-messages/{_pilot}");
        var token = AdminBrowser.AntiforgeryTokenIn(page.Body);

        var refused = await browser.PostFormForDownloadAsync($"/admin/contact-messages/{_pilot}/export", Fields(), null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);

        var file = await browser.PostFormForDownloadAsync($"/admin/contact-messages/{_pilot}/export", Fields(), token);

        Assert.Equal(HttpStatusCode.OK, file.Status);
        Assert.StartsWith("application/json", file.ContentType, StringComparison.Ordinal);
        Assert.Equal("attachment", file.ContentDisposition!.DispositionType);
        Assert.Equal($"pilot-application-{_pilot:N}.json", file.ContentDisposition.FileNameStar ?? file.ContentDisposition.FileName?.Trim('"'));
        Assert.Contains("no-store", file.CacheControl, StringComparison.Ordinal);
        Assert.Equal("nosniff", file.ContentTypeOptions);

        using var json = JsonDocument.Parse(file.Body);
        var application = json.RootElement.GetProperty("application");
        Assert.Equal("wasla-pilot-application", json.RootElement.GetProperty("format").GetString());
        Assert.Equal(_pilot, application.GetProperty("id").GetGuid());
        Assert.Equal(PilotRetentionTestData.RestaurantName, application.GetProperty("restaurantName").GetString());
        Assert.Equal(PilotRetentionTestData.ContactName, application.GetProperty("contactName").GetString());
        Assert.Equal(PilotRetentionTestData.Email(_pilot), application.GetProperty("email").GetString());
        Assert.Equal(PilotRetentionTestData.Phone, application.GetProperty("phone").GetString());
        Assert.Equal("İstanbul", application.GetProperty("cityName").GetString());
        Assert.Equal(PilotRetentionTestData.Note, application.GetProperty("note").GetString());
        Assert.Equal("New", application.GetProperty("status").GetString());
        Assert.True(application.TryGetProperty("deleteOnOrAfterUtc", out _));

        var contact = await browser.PostFormForDownloadAsync($"/admin/contact-messages/{_contact}/export", Fields(), token);
        Assert.Equal(HttpStatusCode.NotFound, contact.Status);
    }

    [Fact]
    public async Task Correct_RefusesInvalidValuesWithLocalizedErrors_AndSavesValidOnes()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var form = await browser.GetAsync($"/admin/contact-messages/{_pilot}/correct");
        Assert.Equal(HttpStatusCode.OK, form.Status);
        Assert.Contains(PilotRetentionTestData.Email(_pilot), form.Body, StringComparison.Ordinal);
        var token = AdminBrowser.AntiforgeryTokenIn(form.Body);
        var valid = new Dictionary<string, string>
        {
            ["RestaurantName"] = "Düzeltilmiş Kebap",
            ["ContactName"] = "Deniz Düzeltilmiş",
            ["Email"] = "deniz.duzeltilmis@retention.test",
            ["Phone"] = "0216 333 44 55",
            ["CityPlateCode"] = "06",
            ["Note"] = "Yeni not"
        };

        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostFormAsync($"/admin/contact-messages/{_pilot}/correct", valid, null)).Status);

        var invalid = await browser.PostFormAsync($"/admin/contact-messages/{_pilot}/correct", new Dictionary<string, string>(valid) { ["Phone"] = "12", ["CityPlateCode"] = "99" }, token);
        var invalidHtml = WebUtility.HtmlDecode(invalid.Body);
        Assert.Equal(HttpStatusCode.OK, invalid.Status);
        Assert.Contains(Resource("Validation.ContactPhoneInvalid"), invalidHtml, StringComparison.Ordinal);
        Assert.Contains(Resource("Validation.PilotCityRequired"), invalidHtml, StringComparison.Ordinal);
        Assert.Contains("aria-invalid=\"true\"", invalid.Body, StringComparison.Ordinal);
        Assert.Equal(PilotRetentionTestData.RestaurantName, (await RowAsync(_pilot))!.RestaurantName);

        var lastActivity = (await RowAsync(_pilot))!.PilotLastActivityAtUtc;
        var saved = await browser.PostFormAsync($"/admin/contact-messages/{_pilot}/correct", valid, token);
        Assert.Equal(HttpStatusCode.Redirect, saved.Status);
        var row = (await RowAsync(_pilot))!;
        Assert.Equal(("Düzeltilmiş Kebap", "Deniz Düzeltilmiş", "deniz.duzeltilmis@retention.test"), (row.RestaurantName, row.FullName, row.Email));
        Assert.Equal(("2163334455", "06", "Yeni not"), (row.Phone, row.CityPlateCode, row.Message));
        Assert.Equal(lastActivity, row.PilotLastActivityAtUtc);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/admin/contact-messages/{_contact}/correct")).Status);
    }

    [Fact]
    public async Task Delete_NeedsTheIdentityConfirmation_AndRecordsTheAdministrator()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var page = await browser.GetAsync($"/admin/contact-messages/{_pilot}");
        var token = AdminBrowser.AntiforgeryTokenIn(page.Body);

        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostFormAsync($"/admin/contact-messages/{_pilot}/delete", Fields(("identityVerified", "true")), null)).Status);

        var unconfirmed = await browser.PostFormAsync($"/admin/contact-messages/{_pilot}/delete", Fields(), token);
        Assert.Equal(HttpStatusCode.Redirect, unconfirmed.Status);
        Assert.NotNull(await RowAsync(_pilot));
        Assert.Contains(Resource("Admin.Pilot.IdentityRequired"), WebUtility.HtmlDecode((await browser.GetAsync($"/admin/contact-messages/{_pilot}")).Body), StringComparison.Ordinal);

        var deleted = await browser.PostFormAsync($"/admin/contact-messages/{_pilot}/delete", Fields(("identityVerified", "true")), token);
        Assert.Equal(HttpStatusCode.Redirect, deleted.Status);
        Assert.Equal("/admin/contact-messages?kind=pilot", deleted.Location!.ToString());
        Assert.Null(await RowAsync(_pilot));
        Assert.Contains(Resource("Admin.Pilot.Deleted"), WebUtility.HtmlDecode((await browser.GetAsync("/admin/contact-messages?kind=pilot")).Body), StringComparison.Ordinal);

        await using var db = _central.CreateContext();
        var adminId = await db.CentralAdminUsers.Where(a => a.Email == AdminWebHost.AdminEmail).Select(a => a.Id).SingleAsync(Ct);
        var record = await db.DataDestructionRecords.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((_pilot, DataDestructionMethod.DataSubjectRequest, DataDestructionActor.CentralAdmin, (Guid?)adminId),
            (record.SubjectId, record.Method, record.Actor, record.ActorUserId));

        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostFormAsync($"/admin/contact-messages/{_pilot}/delete", Fields(("identityVerified", "true")), token)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostFormAsync($"/admin/contact-messages/{_contact}/delete", Fields(("identityVerified", "true")), token)).Status);
        Assert.NotNull(await RowAsync(_contact));
    }

    [Fact]
    public async Task AnonymousAndTenantSessions_ReachNoneOfTheRequestPages_AndChangeNothing()
    {
        // A tenant session's antiforgery token is valid for this host; the Central Admin scheme still refuses it.
        using var anonymous = _host.NewBrowser();
        using var tenantUser = _host.NewBrowser();
        await tenantUser.SignInTenantUserAsync(Guid.NewGuid());

        foreach (var browser in new[] { anonymous, tenantUser })
        {
            foreach (var path in new[] { "/admin/contact-messages/find", $"/admin/contact-messages/{_pilot}/correct" })
            {
                var page = await browser.GetAsync(path);
                Assert.Equal(HttpStatusCode.Redirect, page.Status);
                Assert.Contains("/admin/login", page.Location!.ToString(), StringComparison.Ordinal);
            }

            foreach (var path in new[] { "export", "delete", "status", "record-contact", "correct" })
            {
                var posted = await browser.PostFormAsync($"/admin/contact-messages/{_pilot}/{path}", Fields(("identityVerified", "true"), ("status", "Declined")), null);
                Assert.NotEqual(HttpStatusCode.OK, posted.Status);
                Assert.DoesNotContain(PilotRetentionTestData.RestaurantName, posted.Body, StringComparison.Ordinal);
            }
        }

        var row = (await RowAsync(_pilot))!;
        Assert.Equal((PilotApplicationStatus.New, PilotRetentionTestData.RestaurantName), (row.PilotStatus!.Value, row.RestaurantName));
        await using var db = _central.CreateContext();
        Assert.False(await db.DataDestructionRecords.AnyAsync(Ct));
    }

    private async Task<ContactMessage?> RowAsync(Guid id)
    {
        await using var db = _central.CreateContext();
        return await db.ContactMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, Ct);
    }

    private static Dictionary<string, string> Fields(params (string Key, string Value)[] fields) => fields.ToDictionary(f => f.Key, f => f.Value);

    private static string Resource(string key, string culture = "en-US") =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Single(e => e.Attribute("name")!.Value == key)
            .Element("value")!.Value;
}
