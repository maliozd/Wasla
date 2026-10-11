using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Admin;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;
using Wasla.Web;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Central Admin login does not reveal which accounts exist or are active (WAS-90): an unknown email, an inactive
/// account and a wrong password get the same result and the same page, and every attempt verifies one bcrypt hash: the
/// account's own, or the static dummy hash for an unknown email. Service tests use a SQLite CentralDb; page tests use
/// the real Admin login controller and view (<see cref="TenantSessionWebHost"/>). Accounts and passwords are fake.
/// </summary>
public sealed partial class CentralAdminEnumerationTests : IAsyncLifetime
{
    private const string ActiveEmail = "ops@wasla.test";
    private const string InactiveEmail = "former@wasla.test";
    private const string UnknownEmail = "nobody@wasla.test";
    private const string Password = "Ops-Test-Password-42";

    private readonly CentralTestDatabase _central = new();
    private CentralAdminUser _active = null!;
    private CentralAdminUser _inactive = null!;

    public async ValueTask InitializeAsync()
    {
        await using var db = _central.CreateContext();
        _active = Admin(ActiveEmail, isActive: true);
        _inactive = Admin(InactiveEmail, isActive: false);
        db.CentralAdminUsers.AddRange(_active, _inactive);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _central.Dispose();
        return ValueTask.CompletedTask;
    }

    // --- Service ------------------------------------------------------------------------------------

    [Fact]
    public async Task UnknownInactiveAndWrongPassword_GetTheSameResult_AndEachVerifiesOneHash()
    {
        var verified = new List<string>();

        var unknown = await ValidateAsync(UnknownEmail, Password, verified);
        var inactiveWithItsRightPassword = await ValidateAsync(InactiveEmail, Password, verified);
        var wrongPassword = await ValidateAsync(ActiveEmail, "Wrong-Password-1", verified);

        var expected = new CentralAdminLoginResult(false, null, null, null, "Admin.InvalidCredentials");
        Assert.Equal(expected, unknown);
        Assert.Equal(expected, inactiveWithItsRightPassword);
        Assert.Equal(expected, wrongPassword);
        Assert.Equal(new[] { AuthValidationService.DummyHash, _inactive.PasswordHash, _active.PasswordHash }, verified);
    }

    [Fact]
    public async Task UnknownEmail_TakesTheStaticDummyHashPath()
    {
        var verified = new List<string>();

        await ValidateAsync(UnknownEmail, Password, verified);
        await ValidateAsync("someone-else@wasla.test", "Another-Password-2", verified);

        Assert.Equal(2, verified.Count);
        Assert.All(verified, hash => Assert.Same(AuthValidationService.DummyHash, hash));
        // A real bcrypt hash with the default work factor, so the unknown-email path costs what a known one does.
        Assert.False(BCrypt.Net.BCrypt.PasswordNeedsRehash(AuthValidationService.DummyHash, 11));
    }

    [Fact]
    public async Task ActiveAccount_StillSignsIn_AndAnInactiveOneIsNotRecordedAsALogin()
    {
        var success = await ValidateAsync(ActiveEmail, Password, []);
        await ValidateAsync(InactiveEmail, Password, []);

        Assert.True(success.Succeeded);
        Assert.Equal(_active.Id, success.UserId);
        Assert.Null(success.ErrorCode);
        await using var db = _central.CreateContext();
        Assert.NotNull((await db.CentralAdminUsers.AsNoTracking().SingleAsync(u => u.Id == _active.Id, TestContext.Current.CancellationToken)).LastLoginAt);
        Assert.Null((await db.CentralAdminUsers.AsNoTracking().SingleAsync(u => u.Id == _inactive.Id, TestContext.Current.CancellationToken)).LastLoginAt);
    }

    [Fact]
    public async Task AnUnreadableStoredHash_FailsTheSameWay()
    {
        await using (var db = _central.CreateContext())
        {
            db.CentralAdminUsers.Add(new CentralAdminUser
            {
                Email = "broken@wasla.test",
                NormalizedEmail = "BROKEN@WASLA.TEST",
                DisplayName = "Broken",
                PasswordHash = "not-a-bcrypt-hash",
                IsActive = true
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var context = _central.CreateContext();
        var result = await new CentralAdminAuthService(context, NullLogger<CentralAdminAuthService>.Instance)
            .ValidateAsync("broken@wasla.test", Password, TestContext.Current.CancellationToken);

        Assert.Equal(new CentralAdminLoginResult(false, null, null, null, "Admin.InvalidCredentials"), result);
    }

    // --- Login page ---------------------------------------------------------------------------------

    public static TheoryData<string> Cultures => ["tr-TR", "en-US", "ar-SA", "ru-RU"];

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task LoginPage_IsTheSameForUnknownInactiveAndWrongPassword(string culture)
    {
        await using var web = await TenantSessionWebHost.StartAsync();
        await using (var db = web.Central.CreateContext())
        {
            db.CentralAdminUsers.AddRange(Admin(ActiveEmail, isActive: true), Admin(InactiveEmail, isActive: false));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var unknown = await PostAdminLoginAsync(web, culture, UnknownEmail, Password);
        var inactive = await PostAdminLoginAsync(web, culture, InactiveEmail, Password);
        var wrong = await PostAdminLoginAsync(web, culture, ActiveEmail, "Wrong-Password-1");

        foreach (var response in new[] { unknown, inactive, wrong })
        {
            Assert.Equal(HttpStatusCode.OK, response.Status);
            Assert.Null(response.Location);
            Assert.Equal(unknown.ContentType, response.ContentType);
            Assert.Equal(unknown.SetCookieNames, response.SetCookieNames);
            Assert.Equal(unknown.NormalizedBody, response.NormalizedBody);
        }

        Assert.Contains(Localized(web, culture, "Admin.InvalidCredentials"), unknown.NormalizedBody, StringComparison.Ordinal);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private async Task<CentralAdminLoginResult> ValidateAsync(string email, string password, List<string> verified)
    {
        await using var db = _central.CreateContext();
        var service = new CentralAdminAuthService(
            db,
            NullLogger<CentralAdminAuthService>.Instance,
            (candidate, hash) =>
            {
                verified.Add(hash);
                return BCrypt.Net.BCrypt.Verify(candidate, hash);
            });
        return await service.ValidateAsync(email, password, TestContext.Current.CancellationToken);
    }

    private static CentralAdminUser Admin(string email, bool isActive) => new()
    {
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        DisplayName = email.Split('@')[0],
        // Low work factor: fake test credentials only.
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, workFactor: 4),
        IsActive = isActive
    };

    private static async Task<PageResponse> PostAdminLoginAsync(TenantSessionWebHost web, string culture, string email, string password)
    {
        using var client = web.Client("wasla.local");
        client.Cookies[CookieRequestCultureProvider.DefaultCookieName] =
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)));
        var token = await client.AntiforgeryTokenAsync("/admin/login");
        var response = await client.PostFormAsync("/admin/login",
        [
            new("Email", email),
            new("Password", password),
            new("RememberMe", "false"),
            new("__RequestVerificationToken", token)
        ]);

        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        // The submitted email is echoed in its field and every form carries its own antiforgery token; nothing else
        // may differ.
        body = AntiforgeryValue().Replace(body, "value=\"TOKEN\"").Replace(email, "EMAIL", StringComparison.Ordinal);
        return new PageResponse(
            response.StatusCode,
            response.Headers.Location?.OriginalString,
            response.Content.Headers.ContentType?.ToString(),
            string.Join(",", client.LastSetCookies.Select(c => c.Split('=')[0]).Order(StringComparer.Ordinal)),
            body);
    }

    private static string Localized(TenantSessionWebHost web, string culture, string key)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var value = web.Services.GetRequiredService<IStringLocalizer<SharedResource>>()[key];
            Assert.False(value.ResourceNotFound, $"{key} has no {culture} resource.");
            return value.Value;
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private sealed record PageResponse(HttpStatusCode Status, string? Location, string? ContentType, string SetCookieNames, string NormalizedBody);

    [GeneratedRegex("(?<=name=\"__RequestVerificationToken\" type=\"hidden\" )value=\"[^\"]*\"")]
    private static partial Regex AntiforgeryValue();
}
