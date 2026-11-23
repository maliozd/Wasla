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

    /// <summary>
    /// Stores one <c>New</c> pilot application per time, received then and last active then, in one save; for the
    /// repeated SQL Server rounds, where a save per row would dominate the run time.
    /// </summary>
    public static async Task<List<Guid>> AddPilotsAsync(CentralDbContext db, IEnumerable<DateTime> receivedAtUtc, CancellationToken ct = default)
    {
        var ids = new List<Guid>();
        foreach (var received in receivedAtUtc)
        {
            var id = Guid.NewGuid();
            var application = ContactMessage.CreatePilotApplication(
                id, RestaurantName, ContactName, Email(id), Phone, "34", Note, "tr-TR", received);
            application.MarkNotificationSent(received.AddSeconds(2));
            db.ContactMessages.Add(application);
            ids.Add(id);
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return ids;
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

/// <summary>
/// Fails reads whose command text contains <paramref name="marker"/>: the <paramref name="failure"/> for the read number
/// it is given (1 for the first such read), or nothing when it returns null.
/// </summary>
internal sealed class FailReads(string marker, Func<int, Exception?> failure) : DbCommandInterceptor
{
    private int _reads;

    public int Reads => _reads;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains(marker, StringComparison.Ordinal)
            && failure(Interlocked.Increment(ref _reads)) is { } exception)
        {
            throw exception;
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains(marker, StringComparison.Ordinal)
            && failure(Interlocked.Increment(ref _reads)) is { } exception)
        {
            throw exception;
        }

        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>
/// A <see cref="Microsoft.Data.SqlClient.SqlException"/> with a chosen error number, as SQL Server raises it. SqlClient
/// has no public constructor, so it is built through its internal factory (Microsoft.Data.SqlClient 5.x).
/// </summary>
internal static class SqlServerErrors
{
    public const int DeadlockVictim = 1205;

    public static Microsoft.Data.SqlClient.SqlException Create(int number)
    {
        const System.Reflection.BindingFlags Internal = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var errorType = typeof(Microsoft.Data.SqlClient.SqlError);
        var constructor = errorType.GetConstructors(Internal)
            .Where(c => c.GetParameters() is [{ ParameterType: var first }, ..] && first == typeof(int))
            .OrderBy(c => c.GetParameters().Length)
            .First();
        var arguments = constructor.GetParameters().Select(p => p.ParameterType switch
        {
            var t when t == typeof(int) && p.Position == 0 => (object?)number,
            var t when t == typeof(string) => p.Name == "errorMessage" ? $"Simulated SQL Server error {number}." : "test",
            var t when t == typeof(byte) => (byte)0,
            var t when t == typeof(int) => 0,
            var t when t == typeof(uint) => 0u,
            _ => null
        }).ToArray();
        var error = constructor.Invoke(arguments);

        var collection = Activator.CreateInstance(typeof(Microsoft.Data.SqlClient.SqlErrorCollection), nonPublic: true)!;
        typeof(Microsoft.Data.SqlClient.SqlErrorCollection).GetMethod("Add", Internal)!.Invoke(collection, [error]);
        var create = typeof(Microsoft.Data.SqlClient.SqlException).GetMethod(
            "CreateException",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(Microsoft.Data.SqlClient.SqlErrorCollection), typeof(string)])!;
        return (Microsoft.Data.SqlClient.SqlException)create.Invoke(null, [collection, "16.0"])!;
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
