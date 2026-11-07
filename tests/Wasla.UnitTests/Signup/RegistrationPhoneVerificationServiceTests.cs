using System.Data.Common;
using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sms;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The verification service's handling of failures that HTTP tests cannot provoke (WAS-192): a sender that throws, a
/// request that stops right after the attempt that reaches the limit, and verification rows left by such a stop. The real
/// draft and verification services over a SQLite CentralDb with the fake sender and a test clock; all data is fake.
/// </summary>
public sealed class RegistrationPhoneVerificationServiceTests : IDisposable
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SignupTestClock _clock = new(new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakeSmsOutbox _outbox = new();
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();

    public RegistrationPhoneVerificationServiceTests()
    {
        _connection.Open();
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    // An HttpClient timeout: cancelled, but not by the caller.
    [InlineData("timeout")]
    [InlineData("connection")]
    public async Task ASenderThatThrows_IsAFailedDelivery_ThatLeavesNoUsableCode(string failure)
    {
        var mobile = SignupDraftClient.NewMobileE164();
        var draftId = await SavedDraftAsync(mobile);
        _outbox.ThrowOnSend = failure == "timeout"
            ? new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")
            : new HttpRequestException("Connection refused.");

        await using var db = NewDb();
        var failed = await Phone(db).SendCodeAsync(draftId, Turkish, Ct);

        Assert.Equal(SignupErrorCodes.PhoneDeliveryFailed, failed.ErrorCode);
        Assert.Equal(PhoneVerificationState.NotSent, failed.Status!.State);
        await using var check = NewDb();
        var row = await check.RegistrationPhoneVerifications.AsNoTracking().SingleAsync(r => r.DraftId == draftId, Ct);
        Assert.Null(row.ProtectedCode);
        Assert.Equal(RegistrationPhoneVerificationSendOutcome.Failed,
            (await check.RegistrationPhoneVerificationSends.AsNoTracking().SingleAsync(s => s.DraftId == draftId, Ct)).Outcome);
        // The send still counts: the resend wait applies before another code.
        _outbox.ThrowOnSend = null;
        Assert.Equal(SignupErrorCodes.PhoneResendNotAvailable, (await Phone(db).SendCodeAsync(draftId, Turkish, Ct)).ErrorCode);
    }

    [Fact]
    public async Task TheAttemptThatReachesTheLimit_LocksTheNumber_EvenWhenTheRequestStopsRightAfterIt()
    {
        var mobile = SignupDraftClient.NewMobileE164();
        var draftId = await SavedDraftAsync(mobile);
        var code = await SendAsync(draftId, mobile);
        await using (var db = NewDb())
        {
            for (var i = 0; i < 4; i++)
                Assert.Equal(SignupErrorCodes.PhoneCodeIncorrect, (await Phone(db).VerifyCodeAsync(draftId, Wrong(code), Ct)).ErrorCode);
        }

        // The fifth wrong code: the request ends right after its attempt was counted, before anything else runs.
        var stop = new StopAfterAttempt();
        await using (var stopped = NewDb(stop))
            await Assert.ThrowsAsync<OperationCanceledException>(() => Phone(stopped).VerifyCodeAsync(draftId, Wrong(code), Ct));

        await using var check = NewDb();
        var row = await check.RegistrationPhoneVerifications.AsNoTracking().SingleAsync(r => r.DraftId == draftId, Ct);
        Assert.Equal(5, row.FailedAttempts);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime + new SignupOptions().PhoneVerification.LockoutDuration, row.LockedUntilUtc);
        await using var db2 = NewDb();
        Assert.Equal(SignupErrorCodes.PhoneLocked, (await Phone(db2).VerifyCodeAsync(draftId, code, Ct)).ErrorCode);

        // After the lock the number gets a new code and a full attempt budget; it is not refused until the draft expires.
        _clock.Advance(TimeSpan.FromMinutes(16));
        var fresh = await SendAsync(draftId, mobile);
        Assert.Equal(5, (await Phone(db2).GetStatusAsync(draftId, Ct)).Status!.AttemptsRemaining);
        Assert.True((await Phone(db2).VerifyCodeAsync(draftId, fresh, Ct)).Succeeded);
    }

    [Fact]
    public async Task AttemptsUsedUpWithoutALock_AreTreatedLikeAPassedLock()
    {
        var mobile = SignupDraftClient.NewMobileE164();
        var draftId = await SavedDraftAsync(mobile);
        var code = await SendAsync(draftId, mobile);
        // A row as an earlier build could leave it: every attempt used, no lock, the code still stored.
        await using (var db = NewDb())
        {
            await db.RegistrationPhoneVerifications.Where(r => r.DraftId == draftId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.FailedAttempts, 5), Ct);
        }

        await using var service = NewDb();
        var spent = await Phone(service).VerifyCodeAsync(draftId, code, Ct);

        Assert.Equal(SignupErrorCodes.PhoneNoActiveCode, spent.ErrorCode);
        Assert.Equal(PhoneVerificationState.NotSent, spent.Status!.State);
        _clock.Advance(TimeSpan.FromSeconds(61));
        var fresh = await SendAsync(draftId, mobile);
        Assert.True((await Phone(service).VerifyCodeAsync(draftId, fresh, Ct)).Succeeded);
    }

    [Fact]
    public async Task TheRightCodeOnTheLastAttempt_Verifies_AndLiftsTheLock()
    {
        var mobile = SignupDraftClient.NewMobileE164();
        var draftId = await SavedDraftAsync(mobile);
        var code = await SendAsync(draftId, mobile);
        await using var db = NewDb();
        for (var i = 0; i < 4; i++)
            await Phone(db).VerifyCodeAsync(draftId, Wrong(code), Ct);

        var verified = await Phone(db).VerifyCodeAsync(draftId, code, Ct);

        Assert.True(verified.Succeeded, verified.ErrorCode);
        Assert.Equal(PhoneVerificationState.Verified, verified.Status!.State);
        await using var check = NewDb();
        var row = await check.RegistrationPhoneVerifications.AsNoTracking().SingleAsync(r => r.DraftId == draftId, Ct);
        Assert.Null(row.LockedUntilUtc);
        Assert.NotNull(row.VerifiedAtUtc);
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private async Task<Guid> SavedDraftAsync(string mobile)
    {
        await using var db = NewDb();
        var drafts = Drafts(db);
        var plan = await drafts.SavePlanAsync(null, new RegistrationPlanInput("Starter", "Monthly"), Ct);
        var id = plan.Draft!.Id;
        var token = SentinelApplicant.NewToken();
        Assert.True((await drafts.SaveRestaurantAsync(id, new RegistrationRestaurantInput(
            $"Bizname{token}", ["burger"], "Landline", "212 555 12 34", null, $"s{token}".ToLowerInvariant(), "Germany",
            null, null, null, null, "Berlin", "Mitte", null, "Main Street 1", null, null, null, null, null, null), Ct)).Succeeded);
        Assert.True((await drafts.SaveOwnerAsync(id, new RegistrationOwnerInput
        {
            FullName = "Owner Name",
            Email = $"owner.{token}@sentinel.test",
            MobilePhone = mobile,
            Password = SignupDraftClient.Password,
            ConfirmPassword = SignupDraftClient.Password
        }, Ct)).Succeeded);
        return id;
    }

    private async Task<string> SendAsync(Guid draftId, string mobile)
    {
        await using var db = NewDb();
        var sent = await Phone(db).SendCodeAsync(draftId, Turkish, Ct);
        Assert.True(sent.Succeeded, sent.ErrorCode);
        return _outbox.LatestCodeTo(mobile)!;
    }

    private RegistrationDraftService Drafts(CentralDbContext db) => new(
        db,
        new WaslaPlanCatalog(),
        new SignupReferenceDataService(db),
        new PendingRegistrationService(db, new WaslaPlanCatalog(), new SignupReferenceDataService(db),
            Options.Create(new TenantOnboardingOptions()), NullLogger<PendingRegistrationService>.Instance),
        new FakeSmsSender(_outbox, _clock),
        Options.Create(new SignupOptions()),
        _clock,
        NullLogger<RegistrationDraftService>.Instance);

    private RegistrationPhoneVerificationService Phone(CentralDbContext db) => new(
        db,
        new FakeSmsSender(_outbox, _clock),
        new SmsMessageTemplates(),
        _dataProtection,
        Options.Create(new SignupOptions()),
        _clock,
        NullLogger<RegistrationPhoneVerificationService>.Instance);

    private CentralDbContext NewDb(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

    private static string Wrong(string code) =>
        ((int.Parse(code, CultureInfo.InvariantCulture) + 1) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>Ends the request right after the statement that counts an attempt has run.</summary>
    private sealed class StopAfterAttempt : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"FailedAttempts\" + 1", StringComparison.Ordinal))
                throw new OperationCanceledException("The request ended.");
            return ValueTask.FromResult(result);
        }
    }
}
