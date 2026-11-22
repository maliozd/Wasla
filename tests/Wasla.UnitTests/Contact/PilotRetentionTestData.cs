using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// Fictional pilot applications and contact messages for the retention tests (WAS-222). Every application carries the
/// same recognisable personal values, so a test can assert that none of them reaches a destruction record, a log or the
/// command's output.
/// </summary>
internal static class PilotRetentionTestData
{
    public const string RestaurantName = "Retention Kebap Evi";
    public const string ContactName = "Deniz Saklamaz";
    public const string EmailLocalPart = "deniz.saklamaz";
    public const string Phone = "5321234567";
    public const string Note = "Please call after 3 pm";

    /// <summary>Every personal value an application of this class holds (the email is per row, see <see cref="Email"/>).</summary>
    public static readonly string[] PersonalValues = [RestaurantName, ContactName, EmailLocalPart, Phone, Note, "Saklamaz"];

    public static string Email(Guid id) => $"{EmailLocalPart}.{id.ToString("N")[..6]}@retention.test";

    /// <summary>
    /// Stores a pilot application received at <paramref name="receivedAtUtc"/> whose notification finished, then sets its
    /// status and last activity as the team would have left them.
    /// </summary>
    public static async Task<Guid> AddPilotAsync(
        CentralDbContext db,
        DateTime lastActivityAtUtc,
        PilotApplicationStatus status = PilotApplicationStatus.New,
        DateTime? receivedAtUtc = null,
        string cityPlateCode = "34",
        CancellationToken ct = default)
    {
        var received = receivedAtUtc ?? lastActivityAtUtc;
        var id = Guid.NewGuid();
        var application = ContactMessage.CreatePilotApplication(
            id, RestaurantName, ContactName, Email(id), Phone, cityPlateCode, Note, "tr-TR", received);
        application.MarkNotificationSent(received.AddSeconds(2));
        db.ContactMessages.Add(application);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        await db.ContactMessages.Where(m => m.Id == id).ExecuteUpdateAsync(
            s => s.SetProperty(m => m.PilotStatus, (PilotApplicationStatus?)status)
                .SetProperty(m => m.PilotLastActivityAtUtc, (DateTime?)lastActivityAtUtc),
            ct);
        return id;
    }

    /// <summary>Gives the application a notification claim taken at <paramref name="claimedAtUtc"/>, as a resend would.</summary>
    public static Task ClaimNotificationAsync(CentralDbContext db, Guid id, DateTime claimedAtUtc, CancellationToken ct = default)
    {
        Guid? claim = Guid.NewGuid();
        return db.ContactMessages.Where(m => m.Id == id).ExecuteUpdateAsync(
            s => s.SetProperty(m => m.NotificationStatus, ContactMessageNotificationStatus.Pending)
                .SetProperty(m => m.NotificationAttemptedAtUtc, (DateTime?)claimedAtUtc)
                .SetProperty(m => m.NotificationClaimId, claim),
            ct);
    }

    /// <summary>Stores a contact form message (not a pilot application) received at <paramref name="receivedAtUtc"/>.</summary>
    public static async Task<Guid> AddContactMessageAsync(CentralDbContext db, DateTime receivedAtUtc, CancellationToken ct = default)
    {
        var message = ContactMessage.Create(
            "Old Contact Sender", "Old Contact Restaurant", "old.contact@retention.test", null, ContactTopic.Other, "An old question", "en-US", true, receivedAtUtc);
        message.MarkNotificationSent(receivedAtUtc.AddSeconds(1));
        db.ContactMessages.Add(message);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return message.Id;
    }
}

/// <summary>Fails the delete of one chosen row, as a lost connection or a refused write would.</summary>
internal sealed class FailDeleteOf(Guid id) : DbCommandInterceptor
{
    public int Failures { get; private set; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("ContactMessages", StringComparison.Ordinal)
            && command.Parameters.Cast<DbParameter>().Any(p => p.Value is Guid g ? g == id : string.Equals(p.Value?.ToString(), id.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            Failures++;
            throw new InvalidOperationException("Simulated failure of the delete.");
        }

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>Counts the batch reads of expired applications.</summary>
internal sealed class CountExpiredReads : DbCommandInterceptor
{
    private int _reads;

    public int Reads => _reads;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("FROM \"ContactMessages\"", StringComparison.Ordinal)
            && command.CommandText.Contains("\"PilotLastActivityAtUtc\"", StringComparison.Ordinal)
            && command.CommandText.Contains("LIMIT", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _reads);
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>Keeps every formatted log line, for asserting what a log does and does not say.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Text)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Text)> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception)));
}
