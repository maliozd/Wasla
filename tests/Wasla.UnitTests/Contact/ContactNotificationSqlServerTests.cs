using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
using Wasla.UnitTests.Billing;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// Pilot applications and their team notification on a real SQL Server CentralDb migrated with this build's migrations
/// (WAS-217). Every caller has its own context and connection pool, as separate Web requests or processes would; the
/// free races start all callers at one barrier, and the overlap cases hold one attempt at a chosen point. The server is
/// <c>WASLA_TEST_SQLSERVER</c>, otherwise <c>(localdb)\MSSQLLocalDB</c>. No email leaves the process.
/// </summary>
public sealed class ContactNotificationSqlServerTests : IClassFixture<SqlServerCentralDatabase>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
    private const int Callers = 8;

    private readonly SqlServerCentralDatabase _db;

    public ContactNotificationSqlServerTests(SqlServerCentralDatabase db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSameFormPostedAtOnce_StoresOneRow_AndStartsOneNotificationAttempt()
    {
        await EnsureCityAsync();
        var request = Request(Guid.NewGuid());
        var sender = new ControlledSender();
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var posts = Enumerable.Range(0, Callers).Select(i => Task.Run(async () =>
        {
            await using var db = _db.Open($"Post{i}");
            await go.Task;
            return await Service(db, sender, new ManualClock(Start)).SubmitPilotApplicationAsync(request, Ct);
        }, Ct)).ToList();
        go.SetResult();
        var results = await Task.WhenAll(posts).WaitAsync(Deadline, Ct);

        Assert.Single(results, r => !r.AlreadyReceived);
        Assert.All(results, r => Assert.Equal(request.SubmissionId, r.ApplicationId));
        Assert.Equal(1, sender.Started);
        Assert.Single(sender.Delivered);
        await using var check = _db.Open();
        var row = await check.ContactMessages.AsNoTracking().SingleAsync(m => m.Id == request.SubmissionId, Ct);
        Assert.Equal((ContactMessageNotificationStatus.Sent, (Guid?)null), (row.NotificationStatus, row.NotificationClaimId));
    }

    [Fact]
    public async Task ResendsOfOneNotificationAtOnce_ClaimOneAttempt()
    {
        await EnsureCityAsync();
        var id = await StoreAsync(Start, ContactMessageNotificationStatus.Failed);
        var sender = new ControlledSender { Hold = true };
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var resends = Enumerable.Range(0, Callers).Select(i => Task.Run(async () =>
        {
            await using var db = _db.Open($"Admin{i}");
            await go.Task;
            return await Service(db, sender, new ManualClock(Start.AddMinutes(1))).ResendNotificationAsync(id, Ct);
        }, Ct)).ToList();
        go.SetResult();

        // The winner holds its send open; everyone else has answered by then.
        await sender.FirstStarted.WaitAsync(Deadline, Ct);
        var losers = new List<ContactNotificationResendResult>();
        for (var answered = 0; answered < Callers - 1; answered++)
        {
            var done = await Task.WhenAny(resends).WaitAsync(Deadline, Ct);
            resends.Remove(done);
            losers.Add(await done);
        }

        Assert.All(losers, r => Assert.Equal(ContactNotificationResendOutcome.InProgress, r.Outcome));
        sender.Release();
        var winner = await Assert.Single(resends).WaitAsync(Deadline, Ct);

        Assert.Equal((ContactNotificationResendOutcome.Attempted, (ContactMessageNotificationStatus?)ContactMessageNotificationStatus.Sent), (winner.Outcome, winner.NotificationStatus));
        Assert.Equal(1, sender.Started);
        Assert.Single(sender.Delivered);
    }

    [Fact]
    public async Task ASendStillHangingPastTheStuckWindow_IsCutOffAtItsDeadline_SoAResendDeliversTheOnlyCopy()
    {
        // The review's case: attempt A's SMTP send hangs; sixteen minutes later Central Admin resends. Before the send
        // deadline both copies could arrive. Now A ends at three minutes without delivering, and B is the only copy.
        await EnsureCityAsync();
        var request = Request(Guid.NewGuid());
        var clockA = new ManualClock(Start);
        var hanging = new ControlledSender { Hold = true };

        await using var aDb = _db.Open("AttemptA");
        var attemptA = Service(aDb, hanging, clockA).SubmitPilotApplicationAsync(request, Ct);
        await hanging.FirstStarted.WaitAsync(Deadline, Ct);
        clockA.Advance(TimeSpan.FromMinutes(16));
        var a = await attemptA.WaitAsync(Deadline, Ct);
        Assert.Equal(ContactMessageNotificationStatus.Failed, a.NotificationStatus);

        var working = new ControlledSender();
        await using (var bDb = _db.Open("AttemptB"))
        {
            var b = await Service(bDb, working, new ManualClock(Start.AddMinutes(16))).ResendNotificationAsync(request.SubmissionId, Ct);
            Assert.Equal(ContactNotificationResendOutcome.Attempted, b.Outcome);
        }

        hanging.Release();
        Assert.Equal(1, hanging.Delivered.Count + working.Delivered.Count);
        Assert.Single(working.Delivered);
        await using var check = _db.Open();
        Assert.Equal(ContactMessageNotificationStatus.Sent, (await check.ContactMessages.AsNoTracking().SingleAsync(m => m.Id == request.SubmissionId, Ct)).NotificationStatus);
    }

    [Fact]
    public async Task AStuckAttemptThatRecordsItsOutcomeLate_CannotOverwriteTheNewerAttempt()
    {
        await EnsureCityAsync();
        var request = Request(Guid.NewGuid());
        var hold = new HoldOutcomeWrite();
        var failing = new ControlledSender { Failure = new InvalidOperationException("SMTP down") };

        // Attempt A stores the application; its email fails and its outcome write is held before it runs.
        await using var aDb = _db.Open("StuckA", hold);
        var attemptA = Service(aDb, failing, new ManualClock(Start)).SubmitPilotApplicationAsync(request, Ct);
        await hold.Reached.WaitAsync(Deadline, Ct);

        // Twenty minutes on the row is still pending: B claims it, sends and records Sent.
        var working = new ControlledSender();
        await using (var bDb = _db.Open("NewerB"))
        {
            var b = await Service(bDb, working, new ManualClock(Start.AddMinutes(20))).ResendNotificationAsync(request.SubmissionId, Ct);
            Assert.Equal((ContactNotificationResendOutcome.Attempted, (ContactMessageNotificationStatus?)ContactMessageNotificationStatus.Sent), (b.Outcome, b.NotificationStatus));
        }

        hold.Release();
        Assert.Equal(ContactMessageNotificationStatus.Failed, (await attemptA.WaitAsync(Deadline, Ct)).NotificationStatus);

        await using var check = _db.Open();
        var row = await check.ContactMessages.AsNoTracking().SingleAsync(m => m.Id == request.SubmissionId, Ct);
        Assert.Equal((ContactMessageNotificationStatus.Sent, (Guid?)null), (row.NotificationStatus, row.NotificationClaimId));
        Assert.Single(working.Delivered);
    }

    [Fact]
    public async Task TheResendRule_AsSqlServerEvaluatesIt_MatchesTheRuleForARowAlreadyRead()
    {
        await EnsureCityAsync();
        var nowUtc = Start.UtcDateTime.AddDays(2);
        var ids = new List<(Guid Id, ContactMessageNotificationStatus Status, DateTime Created)>();
        await using (var db = _db.Open("Parity"))
        {
            foreach (var status in Enum.GetValues<ContactMessageNotificationStatus>())
            {
                foreach (var minutes in new double[] { 1, 14.99, 15, 15.01, 60 })
                {
                    var created = nowUtc.AddMinutes(-minutes);
                    var row = ContactMessage.CreatePilotApplication(Guid.NewGuid(), "Parity", "Ayşe Kaya", "ayse@lezzet.test", "5324182790", "34", null, "tr-TR", created);
                    if (status == ContactMessageNotificationStatus.Sent) row.MarkNotificationSent(created);
                    if (status == ContactMessageNotificationStatus.Failed) row.MarkNotificationFailed(created);
                    if (status == ContactMessageNotificationStatus.NotConfigured) row.MarkNotificationNotConfigured(created);
                    db.ContactMessages.Add(row);
                    ids.Add((row.Id, status, created));
                }
            }

            await db.SaveChangesAsync(Ct);
        }

        await using var read = _db.Open();
        var mine = ids.Select(i => i.Id).ToList();
        var byQuery = await read.ContactMessages.Where(m => mine.Contains(m.Id)).Where(ContactNotificationRetry.NeedsAttentionAt(nowUtc)).Select(m => m.Id).ToListAsync(Ct);
        var byRule = ids.Where(i => ContactNotificationRetry.CanResend(i.Status, i.Created, i.Status == ContactMessageNotificationStatus.Pending ? null : i.Created, nowUtc)).Select(i => i.Id);

        Assert.Equal(byRule.Order(), byQuery.Order());
    }

    private async Task EnsureCityAsync()
    {
        await using var db = _db.Open("Setup");
        if (!await db.Cities.AnyAsync(c => c.CountryCode == "TR" && c.PlateCode == "34", Ct))
        {
            db.Cities.Add(new City { CountryCode = "TR", Name = "İstanbul", PlateCode = "34", SortOrder = 34, IsActive = true });
            await db.SaveChangesAsync(Ct);
        }
    }

    private async Task<Guid> StoreAsync(DateTimeOffset createdAt, ContactMessageNotificationStatus status)
    {
        await using var db = _db.Open("Setup");
        var row = ContactMessage.CreatePilotApplication(Guid.NewGuid(), "Lezzet Ocakbaşı", "Ayşe Kaya", "ayse@lezzet.test", "5324182790", "34", null, "tr-TR", createdAt.UtcDateTime);
        if (status == ContactMessageNotificationStatus.Failed)
            row.MarkNotificationFailed(createdAt.UtcDateTime);
        db.ContactMessages.Add(row);
        await db.SaveChangesAsync(Ct);
        return row.Id;
    }

    private static ContactMessageService Service(CentralDbContext db, ControlledSender sender, TimeProvider clock)
    {
        var emailOptions = Options.Create(new EmailOptions { Provider = "Smtp", FromName = "Wasla" });
        var factory = new ContactMessageEmailFactory(
            new EmailTemplateRenderer(),
            emailOptions,
            Options.Create(new ContactOptions { RecipientEmail = "team@wasla.test" }));
        return new ContactMessageService(db, sender, factory, new TurkishProvinceCatalog(db), emailOptions, clock, NullLogger<ContactMessageService>.Instance);
    }

    private static PilotApplicationRequest Request(Guid id) => new(
        id, "Lezzet Ocakbaşı", "Ayşe Kaya", "ayse@lezzet.test", "5324182790", "34", null, "tr-TR");

    /// <summary>Holds the outcome write (the conditional update that matches the attempt's claim) before it runs.</summary>
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
                && command.CommandText.Contains("[NotificationClaimId] = @", StringComparison.Ordinal))
            {
                _reached.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }

            return result;
        }
    }
}
