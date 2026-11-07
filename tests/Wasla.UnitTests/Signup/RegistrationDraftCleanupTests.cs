using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sms;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The cleanup of registration drafts (WAS-192): expired drafts with their verification rows and send records older than
/// a day are deleted in batches, live drafts and unexpired submitted ones are kept, the registrations they became are
/// never touched, and an expired draft is already missing before any cleanup runs. SQLite CentralDb built from the
/// model; <see cref="RegistrationDraftSqlServerTests"/> runs the cleanup on SQL Server too.
/// </summary>
public sealed class RegistrationDraftCleanupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SignupTestClock _clock = new(Now);

    public RegistrationDraftCleanupTests()
    {
        _connection.Open();
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cleanup_DeletesExpiredDraftsWithTheirRows_AndOldSends_AndKeepsEverythingElse()
    {
        var keptRegistration = new SentinelApplicant(PendingRegistrationStatus.AwaitingPayment).Entity;
        var expiredRegistration = new SentinelApplicant(PendingRegistrationStatus.AwaitingPayment).Entity;
        var expired = Draft(Now.AddMinutes(-1));
        var live = Draft(Now.AddHours(1));
        var submittedLive = Draft(Now.AddHours(1), keptRegistration.Id);
        var submittedExpired = Draft(Now.AddSeconds(-1), expiredRegistration.Id);
        await using (var db = NewDb())
        {
            db.PendingRegistrations.AddRange(keptRegistration, expiredRegistration);
            db.RegistrationDrafts.AddRange(expired, live, submittedLive, submittedExpired);
            db.RegistrationPhoneVerifications.AddRange(Verification(expired.Id), Verification(expired.Id, "+905320000002"), Verification(live.Id));
            db.RegistrationPhoneVerificationSends.AddRange(
                Send(expired.Id, Now.AddDays(-1).AddSeconds(-1)),
                Send(live.Id, Now.AddDays(-1).AddSeconds(1)),
                Send(expired.Id, Now.AddMinutes(-5)));
            await db.SaveChangesAsync(Ct);
        }

        RegistrationDraftCleanup.Result result;
        await using (var db = NewDb())
            result = await Cleanup(db).RunOnceAsync(Ct);

        Assert.Equal(new RegistrationDraftCleanup.Result(Drafts: 2, Verifications: 2, Sends: 1), result);
        await using var check = NewDb();
        Assert.Equal(
            new[] { live.Id, submittedLive.Id }.Order(),
            (await check.RegistrationDrafts.Select(d => d.Id).ToListAsync(Ct)).Order());
        Assert.Equal(new[] { live.Id }, await check.RegistrationPhoneVerifications.Select(r => r.DraftId).ToListAsync(Ct));
        // Send records count for a day whatever happened to their draft; only older ones go.
        Assert.Equal(2, await check.RegistrationPhoneVerificationSends.CountAsync(Ct));
        Assert.Equal(2, await check.PendingRegistrations.CountAsync(Ct));
    }

    [Fact]
    public async Task Cleanup_WorksThroughMoreThanOneBatch_AndAGainRunDeletesNothing()
    {
        await using (var db = NewDb())
        {
            for (var i = 0; i < RegistrationDraftCleanup.BatchSize * 2 + 3; i++)
                db.RegistrationDrafts.Add(Draft(Now.AddMinutes(-i - 1)));
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = NewDb())
            Assert.Equal(RegistrationDraftCleanup.BatchSize * 2 + 3, (await Cleanup(db).RunOnceAsync(Ct)).Drafts);
        await using (var db = NewDb())
            Assert.Equal(new RegistrationDraftCleanup.Result(0, 0, 0), await Cleanup(db).RunOnceAsync(Ct));
    }

    [Fact]
    public async Task AnExpiredDraft_IsAlreadyMissingBeforeCleanup()
    {
        var draft = Draft(Now.AddSeconds(-1));
        draft.PlanCode = "Starter";
        draft.BillingPeriod = "Monthly";
        draft.PlanSavedAtUtc = Now.AddHours(-2);
        draft.OwnerMobilePhone = "+905321234567";
        draft.PasswordHash = "fake-hash";
        draft.OwnerSavedAtUtc = Now.AddHours(-2);
        draft.PhoneVerifiedNumber = draft.OwnerMobilePhone;
        draft.PhoneVerifiedAtUtc = Now.AddHours(-1);
        await using (var db = NewDb())
        {
            db.RegistrationDrafts.Add(draft);
            db.RegistrationPhoneVerifications.Add(Verification(draft.Id));
            await db.SaveChangesAsync(Ct);
        }

        await using var service = NewDb();
        var drafts = Drafts(service);
        var phone = Phone(service);

        Assert.Null(await drafts.GetAsync(draft.Id, Ct));
        Assert.Equal(SignupErrorCodes.DraftMissing, (await drafts.SubmitAsync(draft.Id, Ct)).ErrorCode);
        Assert.Equal(SignupErrorCodes.DraftMissing, (await drafts.SavePlanAsync(draft.Id, new RegistrationPlanInput("Starter", "Monthly"), Ct)).ErrorCode);
        Assert.Equal(SignupErrorCodes.DraftMissing, (await phone.SendCodeAsync(draft.Id, CultureInfo.GetCultureInfo("tr-TR"), Ct)).ErrorCode);
        Assert.Equal(SignupErrorCodes.DraftMissing, (await phone.VerifyCodeAsync(draft.Id, "123456", Ct)).ErrorCode);
        await using var check = NewDb();
        Assert.True(await check.RegistrationDrafts.AnyAsync(d => d.Id == draft.Id, Ct));
        Assert.False(await check.PendingRegistrations.AnyAsync(Ct));
    }

    [Fact]
    public void TheCleanupService_IsRegisteredWithTheDraftServices()
    {
        var services = new ServiceCollection();
        services.AddWaslaRegistrationDrafts(new ConfigurationBuilder().Build(), new TestEnvironment(Environments.Production));

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RegistrationDraftCleanupService));
        Assert.Contains(services, d => d.ServiceType == typeof(RegistrationDraftCleanup) && d.Lifetime == ServiceLifetime.Scoped);
    }

    private RegistrationDraftCleanup Cleanup(CentralDbContext db) => new(db, _clock, NullLogger<RegistrationDraftCleanup>.Instance);

    private RegistrationDraftService Drafts(CentralDbContext db) => new(
        db,
        new WaslaPlanCatalog(),
        new SignupReferenceDataService(db),
        new PendingRegistrationService(db, new WaslaPlanCatalog(), new SignupReferenceDataService(db),
            Options.Create(new TenantOnboardingOptions()), NullLogger<PendingRegistrationService>.Instance),
        new FakeSmsSender(new FakeSmsOutbox(), _clock),
        Options.Create(new SignupOptions()),
        _clock,
        NullLogger<RegistrationDraftService>.Instance);

    private RegistrationPhoneVerificationService Phone(CentralDbContext db) => new(
        db,
        new FakeSmsSender(new FakeSmsOutbox(), _clock),
        new SmsMessageTemplates(),
        new EphemeralDataProtectionProvider(),
        Options.Create(new SignupOptions()),
        _clock,
        NullLogger<RegistrationPhoneVerificationService>.Instance);

    private static RegistrationDraft Draft(DateTime expiresAtUtc, Guid? registrationId = null) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAtUtc = expiresAtUtc.AddDays(-1),
        UpdatedAtUtc = expiresAtUtc.AddDays(-1),
        ExpiresAtUtc = expiresAtUtc,
        Revision = 1,
        PendingRegistrationId = registrationId,
        SubmittedAtUtc = registrationId is null ? null : expiresAtUtc.AddHours(-2)
    };

    private static RegistrationPhoneVerification Verification(Guid draftId, string phone = "+905320000001") => new()
    {
        Id = Guid.NewGuid(),
        DraftId = draftId,
        PhoneNumber = phone,
        CreatedAtUtc = Now.AddHours(-2),
        SendCount = 1,
        LastSentAtUtc = Now.AddHours(-2)
    };

    private static RegistrationPhoneVerificationSend Send(Guid draftId, DateTime sentAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        DraftId = draftId,
        PhoneNumber = "+905320000001",
        SentAtUtc = sentAtUtc,
        Outcome = RegistrationPhoneVerificationSendOutcome.Accepted
    };

    private CentralDbContext NewDb() => new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Wasla.UnitTests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
