using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Abstractions.Email;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// <see cref="ContactMessageService"/> over a real SQLite CentralDb: the message is stored before the email, an email
/// failure or missing SMTP never loses it, and a failed save reaches the caller without an email. Fake addresses only.
/// </summary>
public sealed class ContactMessageServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 15, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public ContactMessageServiceTests()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Submit_StoresTheMessage_ThenSendsTheNotification()
    {
        var sender = new RecordingSender();
        await using var db = NewContext();

        var result = await Service(db, sender).SubmitAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ContactMessageNotificationStatus.Sent, result.NotificationStatus);
        var email = Assert.Single(sender.Sent);
        Assert.Equal("team@wasla.test", email.ToEmail);
        Assert.True(email.IsSensitive);
        Assert.True(sender.StoredBeforeSending, "The notification was sent before the message was stored.");

        await using var check = NewContext();
        var stored = await check.ContactMessages.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(result.MessageId, stored.Id);
        Assert.Equal("Ayşe Yılmaz", stored.FullName);
        Assert.Equal("Lezzet Ocakbaşı", stored.RestaurantName);
        Assert.Equal("ayse@lezzet.test", stored.Email);
        Assert.Equal("5446695691", stored.Phone);
        Assert.Equal(ContactTopic.Pricing, stored.Topic);
        Assert.Equal("tr-TR", stored.Culture);
        Assert.Equal(Now.UtcDateTime, stored.CreatedAtUtc);
        Assert.Equal(ContactMessageNotificationStatus.Sent, stored.NotificationStatus);
        Assert.Equal(Now.UtcDateTime, stored.NotificationAttemptedAtUtc);
    }

    [Fact]
    public async Task Submit_WhenTheEmailFails_KeepsTheMessage_AndRecordsTheFailure()
    {
        var sender = new RecordingSender { Failure = new InvalidOperationException("SMTP unavailable for ayse@lezzet.test") };
        var logs = new CapturingLoggerProvider();
        await using var db = NewContext();

        var result = await Service(db, sender, logger: logs).SubmitAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ContactMessageNotificationStatus.Failed, result.NotificationStatus);
        await using var check = NewContext();
        var stored = await check.ContactMessages.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ContactMessageNotificationStatus.Failed, stored.NotificationStatus);
        Assert.NotNull(stored.NotificationAttemptedAtUtc);

        // The log names the message and the failure category, never what the sender typed or the exception text.
        var log = string.Join(Environment.NewLine, logs.Entries);
        Assert.Contains(result.MessageId.ToString(), log, StringComparison.Ordinal);
        foreach (var personal in new[] { "Ayşe", "lezzet", "5446695691", "SMTP unavailable", "Merhaba" })
            Assert.DoesNotContain(personal, log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_WithoutSmtp_KeepsTheMessage_AndSendsNothing()
    {
        var sender = new RecordingSender();
        await using var db = NewContext();

        var result = await Service(db, sender, provider: "Log").SubmitAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ContactMessageNotificationStatus.NotConfigured, result.NotificationStatus);
        Assert.Empty(sender.Sent);
        await using var check = NewContext();
        Assert.Equal(ContactMessageNotificationStatus.NotConfigured, (await check.ContactMessages.SingleAsync(TestContext.Current.CancellationToken)).NotificationStatus);
    }

    [Fact]
    public async Task Submit_WhenStoringFails_Throws_AndSendsNoEmail()
    {
        var sender = new RecordingSender();
        await using (var setup = NewContext())
            await setup.Database.ExecuteSqlRawAsync("DROP TABLE \"ContactMessages\";", TestContext.Current.CancellationToken);
        await using var db = NewContext();

        await Assert.ThrowsAsync<DbUpdateException>(() => Service(db, sender).SubmitAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Submit_OfAnInvalidRequest_StoresNothing()
    {
        var sender = new RecordingSender();
        await using var db = NewContext();

        await Assert.ThrowsAsync<ArgumentException>(() => Service(db, sender).SubmitAsync(
            Request() with { PrivacyNoticeAcknowledged = false }, TestContext.Current.CancellationToken));

        await using var check = NewContext();
        Assert.False(await check.ContactMessages.AnyAsync(TestContext.Current.CancellationToken));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task AdminList_IsNewestFirst_Paged_AndBounded()
    {
        await using (var db = NewContext())
        {
            for (var i = 0; i < 53; i++)
            {
                var message = Wasla.Domain.Entities.Central.ContactMessage.Create(
                    $"Sender {i:00}", "Restaurant", $"sender{i}@example.test", null, ContactTopic.Other, "Hello", "en-US", true,
                    Now.UtcDateTime.AddMinutes(i));
                db.ContactMessages.Add(message);
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = NewContext();
        var service = new CentralAdminContactMessageService(read);

        var first = await service.GetListAsync(1, 500, TestContext.Current.CancellationToken);
        Assert.Equal(AdminContactMessagePaging.MaxPageSize, first.PageSize);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal((53, 2), (first.TotalCount, first.TotalPages));
        Assert.Equal("Sender 52", first.Items[0].FullName);

        var second = await service.GetListAsync(2, 50, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "Sender 02", "Sender 01", "Sender 00" }, second.Items.Select(i => i.FullName));

        var beyond = await service.GetListAsync(9, 50, TestContext.Current.CancellationToken);
        Assert.Equal(2, beyond.Page);
        var before = await service.GetListAsync(-3, 50, TestContext.Current.CancellationToken);
        Assert.Equal(1, before.Page);

        var detail = await service.GetDetailAsync(first.Items[0].Id, TestContext.Current.CancellationToken);
        Assert.Equal("sender52@example.test", detail!.Email);
        Assert.Null(await service.GetDetailAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AdminList_OfNoMessages_IsOneEmptyPage()
    {
        await using var read = NewContext();

        var empty = await new CentralAdminContactMessageService(read).GetListAsync(1, 50, TestContext.Current.CancellationToken);

        Assert.Empty(empty.Items);
        Assert.Equal((0, 1, 1), (empty.TotalCount, empty.TotalPages, empty.Page));
    }

    private CentralDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);

    private ContactMessageService Service(CentralDbContext db, RecordingSender sender, string provider = "Smtp", CapturingLoggerProvider? logger = null)
    {
        var emailOptions = Options.Create(new EmailOptions { Provider = provider, FromName = "Wasla" });
        sender.Connection = _connection;
        var factory = new ContactMessageEmailFactory(new EmailTemplateRenderer(), emailOptions, Options.Create(new ContactOptions { RecipientEmail = "team@wasla.test" }));
        return new ContactMessageService(
            db,
            sender,
            factory,
            emailOptions,
            new FixedTime(Now),
            logger is null
                ? NullLogger<ContactMessageService>.Instance
                : LoggerFactory.Create(builder => builder.AddProvider(logger).SetMinimumLevel(LogLevel.Debug)).CreateLogger<ContactMessageService>());
    }

    private static ContactMessageRequest Request() => new(
        "Ayşe Yılmaz",
        "Lezzet Ocakbaşı",
        "ayse@lezzet.test",
        "5446695691",
        ContactTopic.Pricing,
        "Merhaba, fiyatları öğrenmek istiyoruz.",
        "tr-TR",
        true);

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
                StoredBeforeSending = Convert.ToInt32(command.ExecuteScalar()) == 1;
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
