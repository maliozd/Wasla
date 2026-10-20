using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Admin;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// The Central Admin Pending Registrations pages over real HTTP (WAS-38): each page reads CentralDb once with a
/// projection, never opens a tenant database, and no secret reaches the rendered HTML, the logs or the error page. The
/// provision action still provisions eligible registrations only, for Central Admins only.
/// </summary>
public sealed partial class AdminPendingRegistrationsWebTests : IAsyncLifetime
{
    private static readonly string[] SecretColumns = ["\"PasswordHash\"", "\"SimulatedPaymentReference\"", "\"EncryptedConnectionString\""];

    private readonly CentralTestDatabase _central = new();
    private readonly RecordingTenantDbFactory _tenants = new();
    private readonly RecordingProvisioning _provisioning = new();
    private readonly CapturingLoggerProvider _logs = new();
    private AdminWebHost _host = null!;

    private Guid _provisioned;
    private Guid _paid;
    private Guid _awaiting;

    public async ValueTask InitializeAsync()
    {
        var tenant = _central.AddTenant("provisioned-pide", name: "Provisioned Pide");
        _provisioned = _central.AddRegistration("provisioned-pide", PendingRegistrationStatus.Provisioned, tenant.Id).Id;
        _paid = _central.AddRegistration("paid-lahmacun", PendingRegistrationStatus.PaymentSucceeded, paymentSucceededAt: DateTime.UtcNow.AddHours(-1)).Id;
        _awaiting = _central.AddRegistration("awaiting-doner", PendingRegistrationStatus.AwaitingPayment).Id;
        _host = await AdminWebHost.StartAsync(_central, _tenants, provisioning: _provisioning, logs: _logs);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _tenants.Dispose();
        _central.Dispose();
    }

    [Fact]
    public async Task ListAndDetail_ReadCentralDbOnce_WithoutSecretColumns_AndRenderNoSecret()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var bodies = new List<string>();

        foreach (var path in new[]
                 {
                     "/admin/pending-registrations",
                     "/admin/pending-registrations?filter=PendingPayment",
                     "/admin/pending-registrations?filter=PaymentReceivedSetupPending",
                     "/admin/pending-registrations?filter=Provisioned",
                     $"/admin/pending-registrations/{_provisioned}",
                     $"/admin/pending-registrations/{_paid}",
                     $"/admin/pending-registrations/{_awaiting}"
                 })
        {
            _central.Counter.Reset();
            var page = await browser.GetAsync(path);

            Assert.Equal(HttpStatusCode.OK, page.Status);
            var commands = _central.Counter.Commands;
            // One projection per page, plus the per-request Central Admin session revalidation.
            Assert.Single(commands, sql => !IsSessionValidation(sql));
            Assert.All(commands, sql =>
            {
                foreach (var column in SecretColumns)
                    Assert.DoesNotContain(column, sql, StringComparison.Ordinal);
            });
            bodies.Add(page.Body);
            bodies.Add(WebUtility.HtmlDecode(page.Body));
        }

        var list = WebUtility.HtmlDecode((await browser.GetAsync("/admin/pending-registrations")).Body);
        Assert.Contains("Registration paid-lahmacun", list, StringComparison.Ordinal);
        Assert.Contains("Owner Person", list, StringComparison.Ordinal);
        // The list shows the owner's name, not their email.
        Assert.DoesNotContain("paid-lahmacun@example.test", list, StringComparison.Ordinal);

        var detail = WebUtility.HtmlDecode((await browser.GetAsync($"/admin/pending-registrations/{_provisioned}")).Body);
        Assert.Contains("provisioned-pide@example.test", detail, StringComparison.Ordinal);
        Assert.Contains("https://provisioned-pide.app.wasla.local/auth/login", detail, StringComparison.Ordinal);
        Assert.Contains("Wasla_Tenant_provisioned-pide", detail, StringComparison.Ordinal);

        foreach (var body in bodies)
            AssertNoSecret(body);
        Assert.Empty(_tenants.Opened);
        Assert.Empty(_provisioning.Calls);
    }

    [Fact]
    public async Task Provision_OfAnEligibleRegistration_StillCallsProvisioning_WithTheTenantLoginUrl()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var token = await AntiforgeryTokenFromAsync(browser, _paid);

        var response = await browser.PostAsync($"/admin/pending-registrations/{_paid}/provision", token);

        Assert.Equal(HttpStatusCode.Redirect, response.Status);
        Assert.EndsWith($"/admin/pending-registrations/{_paid}", response.Location!.ToString(), StringComparison.Ordinal);
        var call = Assert.Single(_provisioning.Calls);
        Assert.Equal(_paid, call.RegistrationId);
        Assert.Matches(@"^http://paid-lahmacun\.app\.wasla\.local(:\d+)?/auth/login$", call.PanelLoginUrl);
        Assert.False(call.Force);

        var after = WebUtility.HtmlDecode((await browser.GetAsync(response.Location.ToString())).Body);
        Assert.Contains(Resource("Admin.PendingReg.ProvisionSuccess"), after, StringComparison.Ordinal);
        AssertNoSecret(after);
        Assert.Empty(_tenants.Opened);
    }

    [Fact]
    public async Task Provision_OfAnIneligibleRegistration_IsRefusedBeforeProvisioning()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var token = await AntiforgeryTokenFromAsync(browser, _paid);

        foreach (var id in new[] { _provisioned, _awaiting })
        {
            var response = await browser.PostAsync($"/admin/pending-registrations/{id}/provision", token);

            Assert.Equal(HttpStatusCode.Redirect, response.Status);
            var after = WebUtility.HtmlDecode((await browser.GetAsync(response.Location!.ToString())).Body);
            Assert.Contains(Resource("Admin.PendingReg.NotEligible"), after, StringComparison.Ordinal);
        }

        var missing = await browser.PostAsync($"/admin/pending-registrations/{Guid.NewGuid()}/provision", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Empty(_provisioning.Calls);
    }

    [Fact]
    public async Task AnonymousAndTenantSessions_CannotReadOrProvisionRegistrations()
    {
        using var anonymous = _host.NewBrowser();
        using var tenantUser = _host.NewBrowser();
        await tenantUser.SignInTenantUserAsync(Guid.NewGuid());

        foreach (var browser in new[] { anonymous, tenantUser })
        {
            foreach (var path in new[] { "/admin/pending-registrations", $"/admin/pending-registrations/{_paid}" })
            {
                var page = await browser.GetAsync(path);
                Assert.Equal(HttpStatusCode.Redirect, page.Status);
                Assert.Contains("/admin/login", page.Location!.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("lahmacun", page.Body, StringComparison.Ordinal);
            }

            var post = await browser.PostAsync($"/admin/pending-registrations/{_paid}/provision", antiforgeryToken: null);
            Assert.Equal(HttpStatusCode.Redirect, post.Status);
            Assert.StartsWith("/admin/login", post.Location!.IsAbsoluteUri ? post.Location.AbsolutePath : post.Location.OriginalString, StringComparison.Ordinal);
        }

        Assert.Empty(_provisioning.Calls);
    }

    [Fact]
    public async Task ReadFailure_ShowsTheGenericErrorPage_AndNeitherItNorTheLogsCarryASecret()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var signedIn = _logs.Entries.Count;
        using (var db = _central.CreateContext())
            db.Database.ExecuteSqlRaw("ALTER TABLE \"PendingRegistrations\" RENAME TO \"PendingRegistrations_Unavailable\";");

        foreach (var path in new[] { "/admin/pending-registrations", $"/admin/pending-registrations/{_provisioned}" })
        {
            var page = await browser.GetAsync(path);

            Assert.Equal(HttpStatusCode.InternalServerError, page.Status);
            Assert.Contains(Resource("Errors.UnexpectedTitle"), WebUtility.HtmlDecode(page.Body), StringComparison.Ordinal);
            foreach (var detail in new[] { "no such table", "SQLite", "Exception", "PendingRegistrations", "Data Source" })
                Assert.DoesNotContain(detail, page.Body, StringComparison.OrdinalIgnoreCase);
            AssertNoSecret(page.Body);
        }

        // The failure was logged with its SQL, which names no secret column; no log entry carries a secret value.
        var failureLogs = string.Join(Environment.NewLine, _logs.Entries.Skip(signedIn));
        Assert.Contains("FROM \"PendingRegistrations\"", failureLogs, StringComparison.Ordinal);
        foreach (var column in SecretColumns)
            Assert.DoesNotContain(column, failureLogs, StringComparison.Ordinal);
        AssertNoSecret(string.Join(Environment.NewLine, _logs.Entries));
    }

    [Fact]
    public async Task Logs_OfListDetailAndProvision_CarryNoSecret()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        // The sign-in itself reads the admin's password hash column; only the Pending Registrations requests count.
        var signedIn = _logs.Entries.Count;
        await browser.GetAsync("/admin/pending-registrations");
        await browser.GetAsync($"/admin/pending-registrations/{_provisioned}");
        var token = await AntiforgeryTokenFromAsync(browser, _paid);
        await browser.PostAsync($"/admin/pending-registrations/{_paid}/provision", token);
        await browser.PostAsync($"/admin/pending-registrations/{_provisioned}/provision", token);

        var logs = string.Join(Environment.NewLine, _logs.Entries.Skip(signedIn));
        // EF Core logged the executed commands, so the absence below is meaningful.
        Assert.Contains("FROM \"PendingRegistrations\"", logs, StringComparison.Ordinal);
        foreach (var column in SecretColumns)
            Assert.DoesNotContain(column, logs, StringComparison.Ordinal);
        AssertNoSecret(string.Join(Environment.NewLine, _logs.Entries));
        Assert.DoesNotContain(AdminWebHost.AdminPassword, string.Join(Environment.NewLine, _logs.Entries), StringComparison.Ordinal);
    }

    private static async Task<string> AntiforgeryTokenFromAsync(AdminBrowser browser, Guid eligibleRegistrationId)
    {
        var page = await browser.GetAsync($"/admin/pending-registrations/{eligibleRegistrationId}");
        var match = ProvisionFormToken().Match(page.Body);
        Assert.True(match.Success, "The eligible registration's page has no provision form.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static void AssertNoSecret(string output)
    {
        foreach (var secret in SecretMarkers.All)
            Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
    }

    private static bool IsSessionValidation(string sql) => sql.Contains("FROM \"CentralAdminUsers\"", StringComparison.Ordinal);

    private static string Resource(string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", "SharedResource.en-US.resx"))
            .Root!
            .Elements("data")
            .Single(e => e.Attribute("name")!.Value == key)
            .Element("value")!.Value;

    [GeneratedRegex("id=\"provisionForm\">\\s*<input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex ProvisionFormToken();

    private sealed record ProvisionCall(Guid RegistrationId, bool Force, string? PanelLoginUrl);

    private sealed class RecordingProvisioning : IPendingRegistrationProvisioningService
    {
        private readonly ConcurrentQueue<ProvisionCall> _calls = new();

        public IReadOnlyList<ProvisionCall> Calls => _calls.ToArray();

        public Task<ProvisioningResult> ProvisionAsync(
            Guid registrationId, bool force = false, string? sqlServerOverride = null, string? sqlAuthOverride = null,
            string? panelLoginUrl = null, CancellationToken ct = default)
        {
            _calls.Enqueue(new ProvisionCall(registrationId, force, panelLoginUrl));
            return Task.FromResult(ProvisioningResult.Success(Guid.NewGuid(), "Paid Lahmacun", "paid-lahmacun.app.wasla.local"));
        }
    }
}
