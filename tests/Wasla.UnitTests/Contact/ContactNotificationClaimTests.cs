using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Contact;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// The team notification of a stored message (WAS-217) over a real SQLite CentralDb: one attempt ends at
/// <see cref="ContactNotificationRetry.SendTimeout"/> whether or not the sender honours its token, an attempt records its
/// outcome only while it still owns the row, and the query form of the resend rule agrees with the in-memory form.
/// SQL Server behaviour under real concurrency is in <see cref="ContactNotificationSqlServerTests"/>.
/// </summary>
public sealed class ContactNotificationClaimTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid SubmissionId = Guid.Parse("2f0d4c38-3f5c-4f6e-9a0b-6d1d0d1a9b11");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public ContactNotificationClaimTests()
    {
        _connection.Open();
        using var db = Context();
        db.Database.EnsureCreated();
        db.Cities.Add(new City { Id = 34, CountryCode = "TR", Name = "İstanbul", PlateCode = "34", IsActive = true });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASendThatNeverFinishes_EndsAtTheDeadline_AsFailed_AndDeliversNothing(bool senderIgnoresItsToken)
    {
        var clock = new ManualClock(Now);
        var sender = new ControlledSender { Hold = true, IgnoreCancellation = senderIgnoresItsToken };
        await using var db = Context();

        var submit = Service(db, sender, clock).SubmitPilotApplicationAsync(Request(), Ct);
        await sender.FirstStarted.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        clock.Advance(ContactNotificationRetry.SendTimeout - TimeSpan.FromSeconds(1));
        Assert.False(submit.IsCompleted, "The attempt ended before its deadline.");
        clock.Advance(TimeSpan.FromSeconds(1));

        var result = await submit.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ContactMessageNotificationStatus.Failed, result.NotificationStatus);
        Assert.Empty(sender.Delivered);
        await using var check = Context();
        var row = await check.ContactMessages.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((ContactMessageNotificationStatus.Failed, (Guid?)null), (row.NotificationStatus, row.NotificationClaimId));
        sender.Release();
    }

    [Fact]
    public void TheSendDeadline_IsFarInsideTheStuckWindow()
    {
        // A running attempt must end long before Central Admin may take it for a stuck one.
        Assert.True(ContactNotificationRetry.SendTimeout * 4 <= ContactNotificationRetry.StuckAfter);
    }

    [Fact]
    public async Task AnEarlierAttemptThatFinishesLate_CannotOverwriteTheNewerAttempt()
    {
        // Attempt A stores the application, its email fails, and its outcome write is held back.
        var hold = new HoldOutcomeWrite();
        var logs = new CapturingLoggerProvider();
        var failing = new ControlledSender { Failure = new InvalidOperationException("SMTP down") };
        await using var aDb = Context(hold);
        var attemptA = Service(aDb, failing, new ManualClock(Now), logs).SubmitPilotApplicationAsync(Request(), Ct);
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Twenty minutes on, the row still looks stuck: Central Admin claims attempt B, which sends and records Sent.
        var working = new ControlledSender();
        await using (var bDb = Context())
        {
            var resent = await Service(bDb, working, new ManualClock(Now.AddMinutes(20))).ResendNotificationAsync(SubmissionId, Ct);
            Assert.Equal((ContactNotificationResendOutcome.Attempted, (ContactMessageNotificationStatus?)ContactMessageNotificationStatus.Sent), (resent.Outcome, resent.NotificationStatus));
        }

        hold.Release();
        var a = await attemptA.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(ContactMessageNotificationStatus.Failed, a.NotificationStatus);
        Assert.Single(working.Delivered);
        await using var check = Context();
        var row = await check.ContactMessages.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(ContactMessageNotificationStatus.Sent, row.NotificationStatus);
        Assert.Null(row.NotificationClaimId);
        Assert.Contains(logs.Entries, e => e.Contains("was not recorded, because a newer attempt owns the notification", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheResendRule_AsAQuery_MatchesTheRuleForARowAlreadyRead()
    {
        // Every status, with an attempt time just inside, at and beyond the stuck window, and with no attempt time.
        var nowUtc = Now.UtcDateTime;
        var cases = new List<(Guid Id, ContactMessageNotificationStatus Status, DateTime Created, DateTime? Attempted)>();
        foreach (var status in Enum.GetValues<ContactMessageNotificationStatus>())
        {
            foreach (var minutes in new double[] { 1, 14.99, 15, 15.01, 60 })
            {
                cases.Add((Guid.NewGuid(), status, nowUtc.AddMinutes(-minutes), null));
                cases.Add((Guid.NewGuid(), status, nowUtc.AddHours(-2), nowUtc.AddMinutes(-minutes)));
            }
        }

        await using (var db = Context())
        {
            foreach (var c in cases)
                db.ContactMessages.Add(Row(c.Id, c.Status, c.Created, c.Attempted));
            await db.SaveChangesAsync(Ct);
        }

        await using var read = Context();
        var byQuery = (await read.ContactMessages.Where(ContactNotificationRetry.NeedsAttentionAt(nowUtc)).Select(m => m.Id).ToListAsync(Ct)).ToHashSet();
        var byRule = cases.Where(c => ContactNotificationRetry.CanResend(c.Status, c.Created, c.Attempted, nowUtc)).Select(c => c.Id).ToHashSet();

        Assert.Equal(byRule.Order(), byQuery.Order());
        Assert.NotEmpty(byRule);
        Assert.NotEqual(cases.Count, byRule.Count);
        var stuck = await read.ContactMessages.CountAsync(ContactNotificationRetry.StuckAt(nowUtc), Ct);
        Assert.Equal(cases.Count(c => c.Status == ContactMessageNotificationStatus.Pending && ContactNotificationRetry.CanResend(c.Status, c.Created, c.Attempted, nowUtc)), stuck);
    }

    [Fact]
    public async Task CountStuckNotifications_CountsOnlyPendingAttemptsOlderThanTheWindow()
    {
        var nowUtc = Now.UtcDateTime;
        await using (var db = Context())
        {
            db.ContactMessages.Add(Row(Guid.NewGuid(), ContactMessageNotificationStatus.Pending, nowUtc.AddMinutes(-16), null));
            db.ContactMessages.Add(Row(Guid.NewGuid(), ContactMessageNotificationStatus.Pending, nowUtc.AddMinutes(-2), null));
            db.ContactMessages.Add(Row(Guid.NewGuid(), ContactMessageNotificationStatus.Failed, nowUtc.AddMinutes(-60), null));
            db.ContactMessages.Add(Row(Guid.NewGuid(), ContactMessageNotificationStatus.Sent, nowUtc.AddMinutes(-60), null));
            await db.SaveChangesAsync(Ct);
        }

        await using var read = Context();
        Assert.Equal(1, await Service(read, new ControlledSender(), new ManualClock(Now)).CountStuckNotificationsAsync(Ct));
    }

    private static ContactMessage Row(Guid id, ContactMessageNotificationStatus status, DateTime createdAtUtc, DateTime? attemptedAtUtc)
    {
        var row = ContactMessage.CreatePilotApplication(id, "Lezzet Ocakbaşı", "Ayşe Kaya", "ayse@lezzet.test", "5324182790", "34", null, "tr-TR", createdAtUtc);
        var record = attemptedAtUtc ?? createdAtUtc;
        switch (status)
        {
            case ContactMessageNotificationStatus.Sent: row.MarkNotificationSent(record); break;
            case ContactMessageNotificationStatus.Failed: row.MarkNotificationFailed(record); break;
            case ContactMessageNotificationStatus.NotConfigured: row.MarkNotificationNotConfigured(record); break;
        }

        // A pending row with an attempt time is a claimed attempt that has not recorded an outcome.
        if (status == ContactMessageNotificationStatus.Pending && attemptedAtUtc is not null)
            typeof(ContactMessage).GetProperty(nameof(ContactMessage.NotificationAttemptedAtUtc))!.SetValue(row, attemptedAtUtc);
        return row;
    }

    private CentralDbContext Context(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

    private static ContactMessageService Service(CentralDbContext db, ControlledSender sender, TimeProvider clock, CapturingLoggerProvider? logs = null)
    {
        var emailOptions = Options.Create(new EmailOptions { Provider = "Smtp", FromName = "Wasla" });
        var factory = new ContactMessageEmailFactory(
            new EmailTemplateRenderer(),
            emailOptions,
            Options.Create(new ContactOptions { RecipientEmail = "team@wasla.test" }));
        return new ContactMessageService(
            db,
            sender,
            factory,
            new TurkishProvinceCatalog(db),
            emailOptions,
            clock,
            logs is null
                ? NullLogger<ContactMessageService>.Instance
                : LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Debug)).CreateLogger<ContactMessageService>());
    }

    private static PilotApplicationRequest Request() => new(
        SubmissionId, "Lezzet Ocakbaşı", "Ayşe Kaya", "ayse@lezzet.test", "5324182790", "34", null, "tr-TR");

    /// <summary>Holds the outcome write (the conditional update of the notification status) until released.</summary>
    private sealed class HoldOutcomeWrite : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("\"NotificationClaimId\" = @", StringComparison.Ordinal))
            {
                _reached.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }

            return result;
        }
    }
}
