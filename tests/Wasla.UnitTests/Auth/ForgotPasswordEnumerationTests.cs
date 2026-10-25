using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Email;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Web;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// The tenant forgot-password page does not reveal whether an email belongs to an account (WAS-90): an active user, an
/// inactive user, an unknown email, a user of another tenant and a failed delivery all get the same response, while only
/// an active user of the resolved tenant gets a reset token and an email. Served by the real tenant AuthController, view
/// and password-reset service (<see cref="TenantSessionWebHost"/>), with the email sender replaced by a recorder.
/// Accounts and addresses are fake.
/// </summary>
public sealed class ForgotPasswordEnumerationTests : IAsyncLifetime
{
    private readonly RecordingEmailSender _email = new();
    private TenantSessionWebHost _web = null!;
    private AppUser _active = null!;
    private AppUser _inactive = null!;
    private AppUser _undeliverable = null!;
    private AppUser _betaUser = null!;

    public async ValueTask InitializeAsync()
    {
        _web = await TenantSessionWebHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(_email);
        });
        _active = _web.SeedUser(_web.AlphaId, "owner@alpha.test", UserRole.Owner);
        _inactive = _web.SeedUser(_web.AlphaId, "former@alpha.test", UserRole.Kitchen, isActive: false);
        _undeliverable = _web.SeedUser(_web.AlphaId, "bounce@alpha.test", UserRole.Manager);
        _betaUser = _web.SeedUser(_web.BetaId, "owner@beta.test", UserRole.Owner);
        _email.FailFor = _undeliverable.Email;
    }

    public async ValueTask DisposeAsync() => await _web.DisposeAsync();

    public static TheoryData<string> Cultures => ["tr-TR", "en-US", "ar-SA", "ru-RU"];

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task EveryEmail_GetsTheSameConfirmation_AndOnlyAnActiveUserGetsResetWork(string culture)
    {
        var active = await RequestResetAsync(culture, _active.Email);
        var inactive = await RequestResetAsync(culture, _inactive.Email);
        var unknown = await RequestResetAsync(culture, "nobody@alpha.test");
        var otherTenant = await RequestResetAsync(culture, _betaUser.Email);
        var failedDelivery = await RequestResetAsync(culture, _undeliverable.Email);

        foreach (var response in new[] { inactive, unknown, otherTenant, failedDelivery })
        {
            Assert.Equal(active.Status, response.Status);
            Assert.Equal(active.Location, response.Location);
            Assert.Equal(active.ContentType, response.ContentType);
            Assert.Equal(active.SetCookieNames, response.SetCookieNames);
            Assert.Equal(active.Body, response.Body);
        }

        Assert.Equal(HttpStatusCode.OK, active.Status);
        Assert.Null(active.Location);
        var html = WebUtility.HtmlDecode(active.Body);
        Assert.Contains(Localized(culture, "Auth.ForgotPassword.ConfirmationTitle"), html, StringComparison.Ordinal);
        Assert.Contains(Localized(culture, "Auth.ForgotPassword.ConfirmationDescription"), html, StringComparison.Ordinal);
        Assert.Contains($"dir=\"{(culture == "ar-SA" ? "rtl" : "ltr")}\"", html, StringComparison.Ordinal);

        // Only the active users of this tenant got a token; only the deliverable one got an email.
        Assert.Equal(new[] { _active.Email }, _email.Sent);
        Assert.Equal(1, TokenCount(_web.AlphaId, _active.Id));
        Assert.Equal(1, TokenCount(_web.AlphaId, _undeliverable.Id));
        Assert.Equal(0, TokenCount(_web.AlphaId, _inactive.Id));
        Assert.Equal(0, TokenCount(_web.BetaId, _betaUser.Id));
    }

    [Fact]
    public async Task Antiforgery_IsStillRequired()
    {
        using var client = _web.Client(TenantSessionWebHost.AlphaHost);
        await client.AntiforgeryTokenAsync("/auth/forgot-password");

        var response = await client.PostFormAsync("/auth/forgot-password", [new("Email", _active.Email)]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_email.Sent);
        Assert.Equal(0, TokenCount(_web.AlphaId, _active.Id));
    }

    private async Task<PageResponse> RequestResetAsync(string culture, string email)
    {
        using var client = _web.Client(TenantSessionWebHost.AlphaHost);
        client.Cookies[CookieRequestCultureProvider.DefaultCookieName] =
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)));
        var token = await client.AntiforgeryTokenAsync("/auth/forgot-password");
        var response = await client.PostFormAsync("/auth/forgot-password",
            [new("Email", email), new("__RequestVerificationToken", token)]);
        return new PageResponse(
            response.StatusCode,
            response.Headers.Location?.OriginalString,
            response.Content.Headers.ContentType?.ToString(),
            string.Join(",", client.LastSetCookies.Select(c => c.Split('=')[0]).Order(StringComparer.Ordinal)),
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private int TokenCount(Guid tenantId, Guid userId)
    {
        using var db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlite(_web.Tenants.ConnectionStringFor(tenantId)).Options);
        return db.PasswordResetTokens.Count(t => t.UserId == userId);
    }

    private string Localized(string culture, string key)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var value = _web.Services.GetRequiredService<IStringLocalizer<SharedResource>>()[key];
            Assert.False(value.ResourceNotFound, $"{key} has no {culture} resource.");
            return value.Value;
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private sealed record PageResponse(HttpStatusCode Status, string? Location, string? ContentType, string SetCookieNames, string Body);

    private sealed class RecordingEmailSender : IEmailSender
    {
        private readonly ConcurrentQueue<string> _sent = new();

        public string? FailFor { get; set; }

        public IReadOnlyList<string> Sent => _sent.ToArray();

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            if (string.Equals(message.ToEmail, FailFor, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Simulated delivery failure (test).");
            _sent.Enqueue(message.ToEmail);
            return Task.CompletedTask;
        }
    }
}
