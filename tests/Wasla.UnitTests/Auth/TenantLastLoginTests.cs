using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Contracts.Auth;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.Web.Models.Auth;
using Wasla.Web.Security;
using ApiAuthController = Wasla.Api.Controllers.AuthController;
using TenantAuthController = Wasla.Web.Areas.Tenant.Controllers.AuthController;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Last-login bookkeeping. A login is a Web password sign-in that established the session: the tenant
/// AuthController records it through ITenantLoginRecorder after SignInAsync. Credential validation alone
/// (AuthValidationService, POST /api/auth/validate), the signup welcome link and cookie authentication do not.
/// Each tenant is its own SQLite file, so separate contexts use separate connections. SQLite does not prove
/// SQL Server locking; the conditional UPDATE is what keeps the stored value from moving backwards.
/// </summary>
public sealed class TenantLastLoginTests : IDisposable
{
    private const string Password = "Correct-Horse-9";
    private static readonly DateTime LoginTime = new(2026, 10, 6, 9, 15, 30, DateTimeKind.Utc);

    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly TenantFiles _tenants = new();
    private readonly CapturingLogger<TenantLoginRecorder> _logger = new();

    public void Dispose() => _tenants.Dispose();

    // --- Web login boundary -------------------------------------------------------------------------

    [Fact]
    public async Task WebLogin_Success_SignsInThenRecordsUtcTimestamp()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        var web = WebLogin(_tenantA);

        var result = await web.Controller.Login(LoginModel(user.Email, Password), Ct);

        Assert.IsType<RedirectResult>(result);
        Assert.Equal(AuthSchemes.Tenant, web.Authentication.SignInScheme);
        Assert.Equal(LoginTime, AsUtc((await ReadRowAsync(_tenantA, user.Id)).LastLoginAt));
        // Recorded once, after the session cookie was issued.
        var call = Assert.Single(web.Recorder.Calls);
        Assert.Equal((_tenantA, user.Id), (call.TenantId, call.UserId));
        Assert.True(call.SignedInBefore);
        Assert.Empty(_logger.Entries);
    }

    [Theory]
    [InlineData("wrong-password", true)]
    [InlineData(Password, false)]
    public async Task WebLogin_FailedAuthentication_DoesNotRecord(string password, bool userIsActive)
    {
        var earlier = LoginTime.AddDays(-2);
        var user = await SeedUserAsync(_tenantA, "user@example.test", isActive: userIsActive, lastLoginAt: earlier);
        var web = WebLogin(_tenantA);

        var result = await web.Controller.Login(LoginModel(user.Email, password), Ct);

        Assert.IsType<ViewResult>(result);
        Assert.Null(web.Authentication.SignInScheme);
        Assert.Empty(web.Recorder.Calls);
        Assert.Equal(earlier, AsUtc((await ReadRowAsync(_tenantA, user.Id)).LastLoginAt));
    }

    [Fact]
    public async Task WebLogin_UnknownUserMissingTenantOrInvalidForm_DoesNotRecord()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test");

        var unknown = WebLogin(_tenantA);
        Assert.IsType<ViewResult>(await unknown.Controller.Login(LoginModel("nobody@example.test", Password), Ct));

        var noTenant = WebLogin(tenantId: null);
        Assert.IsType<ViewResult>(await noTenant.Controller.Login(LoginModel(user.Email, Password), Ct));

        var invalidForm = WebLogin(_tenantA);
        invalidForm.Controller.ModelState.AddModelError("Email", "Validation.EmailRequired");
        Assert.IsType<ViewResult>(await invalidForm.Controller.Login(LoginModel(user.Email, Password), Ct));

        Assert.Empty(unknown.Recorder.Calls.Concat(noTenant.Recorder.Calls).Concat(invalidForm.Recorder.Calls));
        Assert.Null((await ReadRowAsync(_tenantA, user.Id)).LastLoginAt);
    }

    [Fact]
    public async Task WebLogin_SignInFailure_DoesNotRecord()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        var web = WebLogin(_tenantA);
        web.Authentication.FailSignIn = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            web.Controller.Login(LoginModel(user.Email, Password), Ct));

        Assert.Empty(web.Recorder.Calls);
        Assert.Null((await ReadRowAsync(_tenantA, user.Id)).LastLoginAt);
    }

    [Fact]
    public async Task WebLogin_CancelledRequest_DoesNotRecord()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        var web = WebLogin(_tenantA);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            web.Controller.Login(LoginModel(user.Email, Password), cts.Token));

        Assert.Empty(web.Recorder.Calls);
        Assert.Null((await ReadRowAsync(_tenantA, user.Id)).LastLoginAt);
    }

    [Fact]
    public async Task SignupWelcomeLink_SignsInWithoutRecordingALogin()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        var web = WebLogin(_tenantA);
        web.Tokens.Payload = new SignupCompletionPayload(_tenantA, user.Id, user.Email, user.FullName, user.Role);

        var result = await web.Controller.Welcome("welcome-token", Ct);

        Assert.Equal("/dashboard", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal(AuthSchemes.Tenant, web.Authentication.SignInScheme);
        Assert.Empty(web.Recorder.Calls);
        Assert.Null((await ReadRowAsync(_tenantA, user.Id)).LastLoginAt);
    }

    [Fact]
    public async Task ExistingSessionCookie_IsNotALogin()
    {
        // Opening /auth/login with a valid tenant cookie only authenticates the cookie and redirects.
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        var web = WebLogin(_tenantA);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("TenantId", _tenantA.ToString()), new Claim("UserId", user.Id.ToString())],
            AuthSchemes.Tenant));
        web.Authentication.AuthenticateResult =
            AuthenticateResult.Success(new AuthenticationTicket(principal, AuthSchemes.Tenant));

        var result = await web.Controller.Login(returnUrl: null);

        Assert.Equal("/dashboard", Assert.IsType<RedirectResult>(result).Url);
        Assert.Null(web.Authentication.SignInScheme);
        Assert.Empty(web.Recorder.Calls);
        Assert.Null((await ReadRowAsync(_tenantA, user.Id)).LastLoginAt);
    }

    [Fact]
    public async Task WebLogin_OldSchemaDatabase_StillSignsIn_AndLogsOnlySafeFailureDetails()
    {
        // A tenant database without the LastLoginAt column (migration not applied yet).
        _tenants.UseOldSchema(_tenantA);
        var user = await SeedOldSchemaUserAsync(_tenantA, "owner@example.test");
        var web = WebLogin(_tenantA);

        var result = await web.Controller.Login(LoginModel("owner@example.test", Password), Ct);

        Assert.Equal("/dashboard", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal(AuthSchemes.Tenant, web.Authentication.SignInScheme);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains(_tenantA.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains(user.Id.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SqliteException), entry.Message, StringComparison.Ordinal);
        AssertNoSensitiveValues(entry.Message, user);
    }

    // --- Credential validation only ---------------------------------------------------------------

    [Fact]
    public async Task ApiCredentialValidation_Succeeds_WithoutRecordingALogin()
    {
        var earlier = LoginTime.AddDays(-2);
        var never = await SeedUserAsync(_tenantA, "never@example.test");
        var seen = await SeedUserAsync(_tenantA, "seen@example.test", lastLoginAt: earlier);
        var api = new ApiAuthController(new FixedTenant(_tenantA), new AuthValidationService(_tenants));

        var first = await api.Validate(new LoginRequest { Email = never.Email, Password = Password }, Ct);
        var second = await api.Validate(new LoginRequest { Email = seen.Email, Password = Password }, Ct);

        Assert.IsType<OkResult>(first);
        Assert.IsType<OkResult>(second);
        Assert.Null((await ReadRowAsync(_tenantA, never.Id)).LastLoginAt);
        Assert.Equal(earlier, AsUtc((await ReadRowAsync(_tenantA, seen.Id)).LastLoginAt));
    }

    [Fact]
    public async Task CredentialValidation_ReadsOnlyAuthenticationColumns_SoOldSchemaStillValidates()
    {
        _tenants.UseOldSchema(_tenantA);
        var user = await SeedOldSchemaUserAsync(_tenantA, "owner@example.test");

        var session = await new AuthValidationService(_tenants).ValidateAsync(_tenantA, user.Email, Password, Ct);

        Assert.NotNull(session);
        Assert.Equal(user.Id, session!.UserId);
        Assert.Equal(UserRole.Owner, session.Role);
    }

    // --- Recorder ---------------------------------------------------------------------------------

    [Fact]
    public async Task Recorder_StoresUtcTimestamp_WithoutTouchingOtherFields()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        var before = await ReadRowAsync(_tenantA, user.Id);

        await Recorder(LoginTime).RecordSuccessfulLoginAsync(_tenantA, user.Id, Ct);

        var after = await ReadRowAsync(_tenantA, user.Id);
        Assert.Equal(LoginTime, AsUtc(after.LastLoginAt));
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.FullName, after.FullName);
        Assert.Equal(before.Role, after.Role);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task Recorder_UsesTheClockInUtc_EvenWhenTheClockHasAnOffset()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        var offsetNow = new DateTimeOffset(2026, 10, 6, 12, 15, 30, TimeSpan.FromHours(3));

        await new TenantLoginRecorder(_tenants, new FixedClock(offsetNow), _logger)
            .RecordSuccessfulLoginAsync(_tenantA, user.Id, Ct);

        Assert.Equal(offsetNow.UtcDateTime, AsUtc((await ReadRowAsync(_tenantA, user.Id)).LastLoginAt));
    }

    [Fact]
    public async Task Recorder_UpdatesOnlyThatUserInThatTenantDatabase()
    {
        // Same user id and email in two tenant databases, plus a second user in the recorded tenant.
        var sharedId = Guid.NewGuid();
        await SeedUserAsync(_tenantA, "same@example.test", id: sharedId);
        await SeedUserAsync(_tenantB, "same@example.test", id: sharedId);
        var colleague = await SeedUserAsync(_tenantA, "colleague@example.test");
        _tenants.Opened.Clear();

        await Recorder(LoginTime).RecordSuccessfulLoginAsync(_tenantA, sharedId, Ct);

        Assert.Equal(new[] { _tenantA }, _tenants.Opened.Distinct());
        Assert.Equal(LoginTime, AsUtc((await ReadRowAsync(_tenantA, sharedId)).LastLoginAt));
        Assert.Null((await ReadRowAsync(_tenantA, colleague.Id)).LastLoginAt);
        Assert.Null((await ReadRowAsync(_tenantB, sharedId)).LastLoginAt);
    }

    [Fact]
    public async Task Recorder_OlderLoginFinishingLater_DoesNotMoveTheTimestampBackwards()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        var newer = LoginTime;
        var older = LoginTime.AddSeconds(-5);

        await Recorder(newer).RecordSuccessfulLoginAsync(_tenantA, user.Id, Ct);
        await Recorder(older).RecordSuccessfulLoginAsync(_tenantA, user.Id, Ct);
        await Recorder(newer).RecordSuccessfulLoginAsync(_tenantA, user.Id, Ct);

        Assert.Equal(newer, AsUtc((await ReadRowAsync(_tenantA, user.Id)).LastLoginAt));
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task ConcurrentSuccessfulWebLogins_AllSignInAndKeepTheNewestTimestamp()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        // Started newest-first and interleaved, so older logins race against and finish after newer ones.
        var times = Enumerable.Range(0, 12).Select(i => LoginTime.AddSeconds(i)).ToArray();
        var startOrder = times.Reverse().Where((_, i) => i % 2 == 0).Concat(times.Where((_, i) => i % 2 == 0));

        var results = await Task.WhenAll(startOrder.Select(time => Task.Run(async () =>
        {
            var web = WebLogin(_tenantA, time);
            var result = await web.Controller.Login(LoginModel(user.Email, Password), Ct);
            return (result, web.Recorder.Calls.Count);
        })));

        Assert.All(results, r => Assert.IsType<RedirectResult>(r.result));
        Assert.All(results, r => Assert.Equal(1, r.Count));
        Assert.Equal(times.Max(), AsUtc((await ReadRowAsync(_tenantA, user.Id)).LastLoginAt));
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task Recorder_DatabaseFailure_IsLoggedSafely_AndNotThrown()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        _tenants.FailUpdates(_tenantA, _ => throw new FakeDbException(
            "Login failed for user 'sa'; Password=" + Password + "; Server=tenant-sql.internal"));

        await Recorder(LoginTime).RecordSuccessfulLoginAsync(_tenantA, user.Id, Ct);

        var entry = Assert.Single(_logger.Entries);
        Assert.Contains(nameof(FakeDbException), entry.Message, StringComparison.Ordinal);
        AssertNoSensitiveValues(entry.Message, user);
        Assert.DoesNotContain("tenant-sql.internal", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sa'", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recorder_CancellationDuringUpdate_Propagates()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _tenants.FailUpdates(_tenantA, _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Recorder(LoginTime).RecordSuccessfulLoginAsync(_tenantA, user.Id, cts.Token));
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task Recorder_DatabaseErrorCausedByCancellation_IsNotSwallowed()
    {
        // SqlClient can surface a cancelled command as a SqlException; that must not become a logged success.
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _tenants.FailUpdates(_tenantA, _ =>
        {
            cts.Cancel();
            throw new FakeDbException("Operation cancelled by user.");
        });

        await Assert.ThrowsAsync<FakeDbException>(() =>
            Recorder(LoginTime).RecordSuccessfulLoginAsync(_tenantA, user.Id, cts.Token));
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task Recorder_NonDatabaseFailure_IsNotHidden()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test");
        _tenants.FailUpdates(_tenantA, _ => throw new InvalidOperationException("programming error"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Recorder(LoginTime).RecordSuccessfulLoginAsync(_tenantA, user.Id, Ct));
        Assert.Empty(_logger.Entries);
    }

    // --- Users page queries -----------------------------------------------------------------------

    [Fact]
    public async Task UsersQueries_ReturnStoredTimestampAndNullForNeverLoggedIn()
    {
        var loggedIn = await SeedUserAsync(_tenantA, "a@example.test");
        var never = await SeedUserAsync(_tenantA, "b@example.test");
        await WebLogin(_tenantA).Controller.Login(LoginModel(loggedIn.Email, Password), Ct);
        var users = new TenantUserRoleService(_tenants, new DefaultPasswordPolicy());

        var list = await users.ListUsersAsync(_tenantA, Ct);

        Assert.Equal(LoginTime, AsUtc(list.Single(u => u.Id == loggedIn.Id).LastLoginAt));
        Assert.Null(list.Single(u => u.Id == never.Id).LastLoginAt);
        Assert.Equal(LoginTime, AsUtc((await users.GetUserAsync(_tenantA, loggedIn.Id, Ct))!.LastLoginAt));
        Assert.Null((await users.GetUserAsync(_tenantA, never.Id, Ct))!.LastLoginAt);
        // Another tenant cannot read the user, so its timestamp is not exposed there either.
        Assert.Null(await users.GetUserAsync(_tenantB, loggedIn.Id, Ct));
    }

    // --- Helpers ----------------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TenantLoginRecorder Recorder(DateTime utcNow) =>
        new(_tenants, new FixedClock(new DateTimeOffset(utcNow)), _logger);

    private WebLoginHarness WebLogin(Guid? tenantId, DateTime? utcNow = null)
    {
        var authentication = new CapturingAuthentication();
        var recorder = new ObservedRecorder(Recorder(utcNow ?? LoginTime), authentication);
        var tokens = new FakeSignupTokens();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("tenant.example.test", 443);
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();

        var controller = new TenantAuthController(
            new FixedTenant(tenantId),
            new AuthValidationService(_tenants),
            recorder,
            tokens,
            new UnusedPasswordReset(),
            new DevelopmentEnvironment(),
            new KeyLocalizer())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NoTempData())
        };

        return new WebLoginHarness(controller, authentication, recorder, tokens);
    }

    private static LoginViewModel LoginModel(string email, string password) =>
        new() { Email = email, Password = password };

    private static DateTime? AsUtc(DateTime? value) =>
        value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static void AssertNoSensitiveValues(string message, AppUser user)
    {
        Assert.DoesNotContain(Password, message, StringComparison.Ordinal);
        Assert.DoesNotContain(user.PasswordHash, message, StringComparison.Ordinal);
        Assert.DoesNotContain(user.Email, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no such column", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Data Source", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".db", message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AppUser> SeedUserAsync(
        Guid tenantId,
        string email,
        bool isActive = true,
        Guid? id = null,
        DateTime? lastLoginAt = null)
    {
        await using var db = await _tenants.CreateAsync(tenantId, Ct);
        var stamp = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var user = new AppUser
        {
            Id = id ?? Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            FullName = "Test User",
            Role = UserRole.Owner,
            IsActive = isActive,
            CreatedAt = stamp,
            UpdatedAt = stamp,
            LastLoginAt = lastLoginAt
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync(Ct);
        return user;
    }

    private async Task<AppUser> SeedOldSchemaUserAsync(Guid tenantId, string email)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            FullName = "Old Schema User",
            Role = UserRole.Owner
        };
        await using var db = await _tenants.CreateAsync(tenantId, Ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AppUsers (Id, Email, PasswordHash, FullName, Role, BranchId, IsActive, CreatedAt, UpdatedAt)
            VALUES ({user.Id}, {user.Email}, {user.PasswordHash}, {user.FullName}, {(int)user.Role}, NULL, 1, {user.CreatedAt}, {user.UpdatedAt})
            """, Ct);
        return user;
    }

    private async Task<AppUser> ReadRowAsync(Guid tenantId, Guid userId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, Ct);
        return await db.AppUsers.AsNoTracking().SingleAsync(u => u.Id == userId, Ct);
    }

    private sealed record WebLoginHarness(
        TenantAuthController Controller,
        CapturingAuthentication Authentication,
        ObservedRecorder Recorder,
        FakeSignupTokens Tokens);

    private sealed record RecorderCall(Guid TenantId, Guid UserId, bool SignedInBefore);

    /// <summary>Real recorder, plus a record of each call and whether the session was already issued.</summary>
    private sealed class ObservedRecorder(TenantLoginRecorder inner, CapturingAuthentication authentication)
        : ITenantLoginRecorder
    {
        public List<RecorderCall> Calls { get; } = new();

        public Task RecordSuccessfulLoginAsync(Guid tenantId, Guid userId, CancellationToken ct)
        {
            Calls.Add(new RecorderCall(tenantId, userId, authentication.SignInScheme is not null));
            return inner.RecordSuccessfulLoginAsync(tenantId, userId, ct);
        }
    }

    private sealed class CapturingAuthentication : IAuthenticationService
    {
        public AuthenticateResult AuthenticateResult { get; set; } = AuthenticateResult.NoResult();
        public bool FailSignIn { get; set; }
        public string? SignInScheme { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult);

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            if (FailSignIn)
                throw new InvalidOperationException("sign-in failed");
            SignInScheme = scheme;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;
    }

    private sealed class FixedTenant(Guid? tenantId) : ICurrentTenantService
    {
        public ResolvedTenantDto? CurrentTenant { get; } =
            tenantId is { } id ? new ResolvedTenantDto(id, "Tenant", "tenant", "tenant.example.test") : null;
    }

    private sealed class FakeSignupTokens : ISignupCompletionTokenService
    {
        public SignupCompletionPayload? Payload { get; set; }

        public string CreateToken(SignupCompletionPayload payload) => "token";

        public SignupCompletionPayload? ValidateAndConsume(string? token) => Payload;
    }

    private sealed class UnusedPasswordReset : ITenantPasswordResetService
    {
        public Task<TenantPasswordResetRequestResult> RequestResetAsync(
            Guid tenantId,
            string email,
            TenantPasswordResetUrlFactory resetUrlFactory,
            string? cultureName = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TenantPasswordResetResult> ResetPasswordAsync(
            Guid tenantId,
            string rawToken,
            string newPassword,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class DevelopmentEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Wasla.Web";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class KeyLocalizer : IStringLocalizer<Wasla.Web.SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.InvariantCulture, name, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private sealed class TenantFiles : ITenantDbContextFactory, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "wasla-last-login-" + Guid.NewGuid().ToString("N"));
        private readonly HashSet<Guid> _created = new();
        private readonly HashSet<Guid> _oldSchema = new();
        private readonly Dictionary<Guid, Action<DbCommand>> _updateFailures = new();
        private readonly object _gate = new();

        public List<Guid> Opened { get; } = new();

        public void UseOldSchema(Guid tenantId) => _oldSchema.Add(tenantId);

        public void FailUpdates(Guid tenantId, Action<DbCommand> fail) => _updateFailures[tenantId] = fail;

        public async Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            var path = Path.Combine(_root, customerId.ToString("N") + ".db");
            bool create;
            lock (_gate)
            {
                Opened.Add(customerId);
                create = _created.Add(customerId);
            }

            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            if (create)
            {
                Directory.CreateDirectory(_root);
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    PRAGMA journal_mode = WAL;
                    CREATE TABLE AppUsers (
                        Id TEXT NOT NULL CONSTRAINT PK_AppUsers PRIMARY KEY,
                        Email TEXT NOT NULL,
                        PasswordHash TEXT NOT NULL,
                        FullName TEXT NOT NULL,
                        Role INTEGER NOT NULL,
                        BranchId TEXT NULL,
                        IsActive INTEGER NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL{(_oldSchema.Contains(customerId) ? "" : ",\n    LastLoginAt TEXT NULL")}
                    );
                    CREATE UNIQUE INDEX IX_AppUsers_Email ON AppUsers (Email);
                    """;
                await command.ExecuteNonQueryAsync(ct);
            }

            var builder = new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connectionString);
            if (_updateFailures.TryGetValue(customerId, out var fail))
                builder.AddInterceptors(new FailUpdateInterceptor(fail));
            return new TenantDbContext(builder.Options);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A temp folder left behind is harmless; never fail a test on cleanup.
            }
        }
    }

    private sealed class FailUpdateInterceptor(Action<DbCommand> fail) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                fail(command);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_gate) return _entries.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
