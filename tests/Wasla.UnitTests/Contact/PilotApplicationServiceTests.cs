using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Abstractions.Email;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// Pilot applications (WAS-217) through <see cref="ContactMessageService"/> over a real SQLite CentralDb: the application
/// is stored before the team is emailed, the same form stores once, the city must be an active province of the catalog,
/// a failed or unconfigured email keeps the application, and Central Admin's resend sends once and only when it should.
/// Fake addresses only; nothing is sent anywhere.
/// </summary>
public sealed class PilotApplicationServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public PilotApplicationServiceTests()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Cities.AddRange(
            new City { Id = 34, CountryCode = "TR", Name = "İstanbul", PlateCode = "34", IsActive = true },
            new City { Id = 6, CountryCode = "TR", Name = "Ankara", PlateCode = "06", IsActive = true },
            new City { Id = 35, CountryCode = "TR", Name = "İzmir", PlateCode = "35", IsActive = true },
            new City { Id = 7, CountryCode = "TR", Name = "Antalya", PlateCode = "07", IsActive = false });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Submit_StoresThePilotApplication_ThenEmailsThePilotAddress()
    {
        var sender = new RecordingSender();
        await using var db = NewContext();

        var result = await Service(db, sender, pilotRecipient: "pilot-team@wasla.test").SubmitPilotApplicationAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(result.AlreadyReceived);
        Assert.Equal(ContactMessageNotificationStatus.Sent, result.NotificationStatus);
        var email = Assert.Single(sender.Sent);
        Assert.Equal("pilot-team@wasla.test", email.ToEmail);
        Assert.True(email.IsSensitive);
        Assert.DoesNotContain("Lezzet", email.Subject, StringComparison.Ordinal);
        // WAS-222: the email names the record, not what the applicant typed.
        Assert.Contains(Request().SubmissionId.ToString(), email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Lezzet", email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("532 418", email.TextBody, StringComparison.Ordinal);
        Assert.True(sender.StoredBeforeSending, "The notification was sent before the application was stored.");

        await using var check = NewContext();
        var stored = await check.ContactMessages.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Request().SubmissionId, stored.Id);
        Assert.Equal(ContactTopic.PilotApplication, stored.Topic);
        Assert.Equal("Lezzet Ocakbaşı", stored.RestaurantName);
        Assert.Equal("Ayşe Kaya", stored.FullName);
        Assert.Equal("5324182790", stored.Phone);
        Assert.Equal("34", stored.CityPlateCode);
        Assert.Equal("Hafta içi 15.00’ten sonra aranabiliriz.", stored.Message);
        Assert.False(stored.PrivacyNoticeAcknowledged);
        Assert.Equal("tr-TR", stored.Culture);
        Assert.Equal(ContactMessageNotificationStatus.Sent, stored.NotificationStatus);
        // An application starts no signup, payment or provisioning.
        Assert.False(await check.PendingRegistrations.AnyAsync(TestContext.Current.CancellationToken));
        Assert.False(await check.Tenants.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Submit_WithoutAPilotAddress_UsesTheContactRecipient_AndAnEmptyNoteIsStoredEmpty()
    {
        var sender = new RecordingSender();
        await using var db = NewContext();

        await Service(db, sender).SubmitPilotApplicationAsync(Request() with { Note = "   " }, TestContext.Current.CancellationToken);

        Assert.Equal("team@wasla.test", Assert.Single(sender.Sent).ToEmail);
        await using var check = NewContext();
        Assert.Equal(string.Empty, (await check.ContactMessages.SingleAsync(TestContext.Current.CancellationToken)).Message);
    }

    [Fact]
    public async Task TheSameFormPostedAgain_StoresOne_AndEmailsOnce()
    {
        var sender = new RecordingSender();
        await using (var db = NewContext())
            await Service(db, sender).SubmitPilotApplicationAsync(Request(), TestContext.Current.CancellationToken);

        await using (var db = NewContext())
        {
            var again = await Service(db, sender).SubmitPilotApplicationAsync(Request() with { Note = "A second click" }, TestContext.Current.CancellationToken);
            Assert.True(again.AlreadyReceived);
            Assert.Equal(ContactMessageNotificationStatus.Sent, again.NotificationStatus);
        }

        Assert.Single(sender.Sent);
        await using var check = NewContext();
        var stored = await check.ContactMessages.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Hafta içi 15.00’ten sonra aranabiliriz.", stored.Message);
    }

    [Fact]
    public async Task WhenAnotherPostStoresTheSameFormFirst_TheKeyConflictIsAnsweredAsReceived_AndNoSecondEmailIsSent()
    {
        var sender = new RecordingSender();
        // The other post commits between this one's existence check and its insert: the insert meets the primary key.
        await using var db = new CentralDbContext(new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new StoreTheSameFormFirst(_connection))
            .Options);

        var result = await Service(db, sender).SubmitPilotApplicationAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(result.AlreadyReceived);
        Assert.Empty(sender.Sent);
        await using var check = NewContext();
        Assert.Equal(1, await check.ContactMessages.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFailedInsertThatStoredNothing_IsNotMistakenForADuplicate()
    {
        var sender = new RecordingSender();
        await using var db = new CentralDbContext(new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new FailEverySave())
            .Options);

        await Assert.ThrowsAsync<DbUpdateException>(() => Service(db, sender).SubmitPilotApplicationAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Empty(sender.Sent);
    }

    private sealed class StoreTheSameFormFirst(SqliteConnection connection) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await using var other = Context(connection);
                other.ContactMessages.Add(ContactMessage.CreatePilotApplication(
                    SubmissionId, "Lezzet Ocakbaşı", "Ayşe Kaya", "ayse@lezzet.test", "5324182790", "34", null, "tr-TR", Now.UtcDateTime));
                await other.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class FailEverySave : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("CentralDb timed out.");
    }

    [Theory]
    [InlineData("07")] // in the catalog, but not active
    [InlineData("81")] // a plate code the catalog does not have
    [InlineData("İstanbul")]
    public async Task ACityOutsideTheActiveCatalog_IsRefused_AndNothingIsStoredOrSent(string city)
    {
        var sender = new RecordingSender();
        await using var db = NewContext();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Service(db, sender).SubmitPilotApplicationAsync(Request() with { CityPlateCode = city }, TestContext.Current.CancellationToken));

        Assert.Empty(sender.Sent);
        await using var check = NewContext();
        Assert.False(await check.ContactMessages.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenTheEmailFails_TheApplicationIsKept_TheFailureIsRecorded_AndTheLogHasNoPersonalData()
    {
        var sender = new RecordingSender { Failure = new InvalidOperationException("SMTP refused ayse@lezzet.test") };
        var logs = new CapturingLoggerProvider();
        await using var db = NewContext();

        var result = await Service(db, sender, logger: logs).SubmitPilotApplicationAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ContactMessageNotificationStatus.Failed, result.NotificationStatus);
        await using var check = NewContext();
        var stored = await check.ContactMessages.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ContactMessageNotificationStatus.Failed, stored.NotificationStatus);
        Assert.Equal(Now.UtcDateTime, stored.NotificationAttemptedAtUtc);

        var log = string.Join(Environment.NewLine, logs.Entries);
        Assert.Contains(result.ApplicationId.ToString(), log, StringComparison.Ordinal);
        Assert.Contains("Topic=PilotApplication", log, StringComparison.Ordinal);
        Assert.Contains("FailureCategory=", log, StringComparison.Ordinal);
        foreach (var personal in new[] { "Ayşe", "Lezzet", "5324182790", "532 418", "ayse@", "SMTP refused", "Hafta" })
            Assert.DoesNotContain(personal, log, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// WAS-222: the team notification carries the record id, the time received and a Central Admin path, and nothing the
    /// applicant typed: a copy in a mailbox or at the SMTP provider would outlive the 90-day retention and a deletion on
    /// request. Every field holds a value found nowhere else, checked in the subject, both bodies, the Central Admin path,
    /// the logs of a sent, a failed and a resent notification, and the exception of a refused submission.
    /// </summary>
    [Fact]
    public async Task TheNotification_AndTheLogs_CarryNothingTheApplicantTyped()
    {
        var id = Guid.Parse("1f2e3d4c-5b6a-4978-8a9b-0c1d2e3f4a5b");
        var request = new PilotApplicationRequest(
            id,
            "Qx7Restoran Sentinel",
            "Qx7Yetkili Sentinel",
            "qx7.sentinel@pilot.test",
            "5398761204",
            "35",
            "Qx7Not sentinel text",
            "tr-TR");
        string[] typed = ["Qx7", "Sentinel", "sentinel", "qx7", "5398761204", "539 876", "876 12", "İzmir", "Izmir", "@pilot.test"];
        var logs = new CapturingLoggerProvider();

        var sender = new RecordingSender();
        await using (var db = NewContext())
            await Service(db, sender, logger: logs).SubmitPilotApplicationAsync(request, TestContext.Current.CancellationToken);
        var email = Assert.Single(sender.Sent);

        foreach (var text in new[] { email.Subject, email.TextBody, email.HtmlBody })
        {
            foreach (var value in typed)
                Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("35", email.TextBody, StringComparison.Ordinal);
        Assert.Contains(id.ToString(), email.TextBody, StringComparison.Ordinal);
        Assert.Contains("2026-11-20 09:00 UTC", email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", email.TextBody + email.HtmlBody, StringComparison.Ordinal);
        var paths = System.Text.RegularExpressions.Regex.Matches(email.TextBody + email.HtmlBody, @"/admin/[^\s)<""]*")
            .Select(m => m.Value)
            .Distinct()
            .ToList();
        Assert.Equal(new[] { $"/admin/contact-messages/{id}" }, paths);

        // A failed notification, then Central Admin's resend.
        await using (var setup = NewContext())
            await setup.ContactMessages.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        var failing = new RecordingSender { Failure = new InvalidOperationException("SMTP refused the message") };
        await using (var db = NewContext())
            await Service(db, failing, logger: logs).SubmitPilotApplicationAsync(request, TestContext.Current.CancellationToken);
        var resent = new RecordingSender();
        await using (var db = NewContext())
            await Service(db, resent, logger: logs, now: Now.AddHours(1)).ResendNotificationAsync(id, TestContext.Current.CancellationToken);
        var again = Assert.Single(resent.Sent);
        foreach (var value in typed)
            Assert.DoesNotContain(value, again.Subject + again.TextBody + again.HtmlBody, StringComparison.OrdinalIgnoreCase);

        // A refused submission (a province that is not active).
        await using (var db = NewContext())
        {
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => Service(db, new RecordingSender(), logger: logs)
                .SubmitPilotApplicationAsync(request with { SubmissionId = Guid.NewGuid(), CityPlateCode = "07" }, TestContext.Current.CancellationToken));
            foreach (var value in typed)
                Assert.DoesNotContain(value, refused.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        var log = string.Join(Environment.NewLine, logs.Entries);
        Assert.Contains(id.ToString(), log, StringComparison.Ordinal);
        foreach (var value in typed)
            Assert.DoesNotContain(value, log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithoutSmtp_TheApplicationIsKept_AndNothingClaimsDelivery()
    {
        var sender = new RecordingSender();
        await using var db = NewContext();

        var result = await Service(db, sender, provider: "Log").SubmitPilotApplicationAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ContactMessageNotificationStatus.NotConfigured, result.NotificationStatus);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task WhenStoringFails_ItThrows_AndSendsNoEmail()
    {
        var sender = new RecordingSender();
        await using (var setup = NewContext())
            await setup.Database.ExecuteSqlRawAsync("DROP TABLE \"ContactMessages\";", TestContext.Current.CancellationToken);
        await using var db = NewContext();

        await Assert.ThrowsAnyAsync<Exception>(() => Service(db, sender).SubmitPilotApplicationAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Resend_AfterAFailure_SendsOnce_AndRecordsTheOutcome()
    {
        var failing = new RecordingSender { Failure = new InvalidOperationException("down") };
        await using (var db = NewContext())
            await Service(db, failing).SubmitPilotApplicationAsync(Request(), TestContext.Current.CancellationToken);

        var working = new RecordingSender();
        await using (var db = NewContext())
        {
            var resent = await Service(db, working).ResendNotificationAsync(Request().SubmissionId, TestContext.Current.CancellationToken);
            Assert.Equal(ContactNotificationResendOutcome.Attempted, resent.Outcome);
            Assert.Equal(ContactMessageNotificationStatus.Sent, resent.NotificationStatus);
        }

        Assert.Single(working.Sent);
        await using (var db = NewContext())
        {
            // Sent once: a second request sends nothing.
            var again = await Service(db, working).ResendNotificationAsync(Request().SubmissionId, TestContext.Current.CancellationToken);
            Assert.Equal(ContactNotificationResendOutcome.AlreadySent, again.Outcome);
        }

        Assert.Single(working.Sent);
    }

    [Fact]
    public async Task Resend_WhileAnAttemptIsRecent_SendsNothing_ButAStuckAttemptCanBeResent()
    {
        var sender = new RecordingSender();
        await using (var db = NewContext())
        {
            // Stored, but the process stopped before the email: still Pending.
            db.ContactMessages.Add(ContactMessage.CreatePilotApplication(
                Request().SubmissionId, "Lezzet Ocakbaşı", "Ayşe Kaya", "ayse@lezzet.test", "5324182790", "34", null, "tr-TR",
                Now.UtcDateTime.AddMinutes(-5)));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = NewContext())
        {
            var recent = await Service(db, sender).ResendNotificationAsync(Request().SubmissionId, TestContext.Current.CancellationToken);
            Assert.Equal(ContactNotificationResendOutcome.InProgress, recent.Outcome);
        }

        Assert.Empty(sender.Sent);
        await using (var db = NewContext())
        {
            var later = await Service(db, sender, now: Now.AddMinutes(20)).ResendNotificationAsync(Request().SubmissionId, TestContext.Current.CancellationToken);
            Assert.Equal(ContactNotificationResendOutcome.Attempted, later.Outcome);
        }

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Resend_OfAnUnknownMessage_IsNotFound()
    {
        await using var db = NewContext();

        var result = await Service(db, new RecordingSender()).ResendNotificationAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(ContactNotificationResendOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task AdminList_SeparatesPilotApplicationsFromContactMessages_AndTheDetailNamesTheCity()
    {
        await using (var db = NewContext())
        {
            db.ContactMessages.Add(ContactMessage.Create("Mehmet Demir", "Demir Kebap", "mehmet@demir.test", null, ContactTopic.Other, "Merhaba", "tr-TR", true, Now.UtcDateTime));
            var pilot = ContactMessage.CreatePilotApplication(Request().SubmissionId, "Lezzet Ocakbaşı", "Ayşe Kaya", "ayse@lezzet.test", "5324182790", "34", null, "tr-TR", Now.UtcDateTime);
            pilot.MarkNotificationFailed(Now.UtcDateTime);
            db.ContactMessages.Add(pilot);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = NewContext();
        var service = new CentralAdminContactMessageService(read, new FixedTime(Now));

        Assert.Equal(2, (await service.GetListAsync(1, 50, AdminContactMessageFilter.All, TestContext.Current.CancellationToken)).TotalCount);
        var pilots = await service.GetListAsync(1, 50, AdminContactMessageFilter.PilotApplications, TestContext.Current.CancellationToken);
        Assert.Equal("Lezzet Ocakbaşı", Assert.Single(pilots.Items).RestaurantName);
        var contacts = await service.GetListAsync(1, 50, AdminContactMessageFilter.ContactMessages, TestContext.Current.CancellationToken);
        Assert.Equal("Demir Kebap", Assert.Single(contacts.Items).RestaurantName);

        var detail = await service.GetDetailAsync(Request().SubmissionId, TestContext.Current.CancellationToken);
        Assert.Equal(("34", "İstanbul"), (detail!.CityPlateCode, detail.CityName));
        Assert.True(detail.CanResendNotification);
    }

    private CentralDbContext NewContext() => Context(_connection);

    private static CentralDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(connection).Options);

    private ContactMessageService Service(
        CentralDbContext db,
        RecordingSender sender,
        string provider = "Smtp",
        string? pilotRecipient = null,
        CapturingLoggerProvider? logger = null,
        DateTimeOffset? now = null)
    {
        var emailOptions = Options.Create(new EmailOptions { Provider = provider, FromName = "Wasla" });
        sender.Connection ??= _connection;
        var factory = new ContactMessageEmailFactory(
            new EmailTemplateRenderer(),
            emailOptions,
            Options.Create(new ContactOptions { RecipientEmail = "team@wasla.test", PilotRecipientEmail = pilotRecipient }));
        return new ContactMessageService(
            db,
            sender,
            factory,
            new TurkishProvinceCatalog(db),
            emailOptions,
            new FixedTime(now ?? Now),
            logger is null
                ? NullLogger<ContactMessageService>.Instance
                : LoggerFactory.Create(builder => builder.AddProvider(logger).SetMinimumLevel(LogLevel.Debug)).CreateLogger<ContactMessageService>());
    }

    private static readonly Guid SubmissionId = Guid.Parse("9a70d5c6-9a80-40ce-a134-8cdf80a3f8b2");

    private static PilotApplicationRequest Request() => new(
        SubmissionId,
        "Lezzet Ocakbaşı",
        "Ayşe Kaya",
        "ayse@lezzet.test",
        "5324182790",
        "34",
        "Hafta içi 15.00’ten sonra aranabiliriz.",
        "tr-TR");

    private sealed class RecordingSender : IEmailSender
    {
        private readonly ConcurrentQueue<EmailMessage> _sent = new();

        public Exception? Failure { get; init; }

        public SqliteConnection? Connection { get; set; }

        public bool StoredBeforeSending { get; private set; }

        public IReadOnlyList<EmailMessage> Sent => _sent.ToArray();

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            using (var command = Connection!.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM \"ContactMessages\"";
                StoredBeforeSending = Convert.ToInt32(command.ExecuteScalar()) >= 1;
            }

            if (Failure is not null)
                throw Failure;

            _sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
