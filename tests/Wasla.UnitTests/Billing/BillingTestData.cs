using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Billing;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;
using CentralTenant = Wasla.Domain.Entities.Central.Tenant;

namespace Wasla.UnitTests.Billing;

/// <summary>Fake registrations, tenants and prices for billing tests. No real contact data or provider values.</summary>
internal static class BillingTestData
{
    public static readonly DateTime Now = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);
    public static readonly BillingPrice MonthlyPrice = new(999.00m, "TRY");
    public static readonly BillingProviderScope Sandbox = new(BillingProvider.Iyzico, BillingEnvironment.Sandbox);
    public static readonly BillingProviderScope Production = new(BillingProvider.Iyzico, BillingEnvironment.Production);

    public static PendingRegistration Registration(
        PendingRegistrationStatus status = PendingRegistrationStatus.AwaitingPayment,
        Guid? tenantId = null)
    {
        var id = Guid.NewGuid();
        var slug = "billing-" + id.ToString("N")[..10];
        return new PendingRegistration
        {
            Id = id,
            PlanCode = "Pro",
            BillingPeriod = BillingSubscription.MonthlyPeriod,
            BusinessName = "Billing Test Restaurant",
            Slug = slug,
            PrimaryDomain = slug + ".app.wasla.local",
            DatabaseName = "Wasla_BillingTest_" + id.ToString("N")[..12],
            BusinessPhone = "5550000000",
            Country = "TR",
            City = "Istanbul",
            District = "Kadikoy",
            OwnerFullName = "Test Owner",
            OwnerEmail = "owner@example.test",
            PasswordHash = "not-a-real-hash",
            Status = status,
            CreatedAtUtc = Now.AddHours(-1),
            ExpiresAtUtc = Now.AddDays(7),
            TenantId = tenantId
        };
    }

    public static CentralTenant Tenant(Guid id) => new()
    {
        Id = id,
        Name = "Billing Test Tenant",
        Slug = "tenant-" + id.ToString("N")[..10],
        PrimaryDomain = "tenant-" + id.ToString("N")[..10] + ".app.wasla.local",
        DatabaseName = "Wasla_BillingTenant_" + id.ToString("N")[..12],
        EncryptedConnectionString = "fake-encrypted-value"
    };

    /// <summary>A subscription moved through allowed steps into <paramref name="status"/>.</summary>
    public static BillingSubscription SubscriptionIn(SubscriptionStatus status, DateTime? observedAtUtc = null)
    {
        var subscription = BillingSubscription.StartPending(
            Guid.NewGuid(),
            BillingProvider.Iyzico,
            BillingEnvironment.Sandbox,
            "Pro",
            BillingSubscription.MonthlyPeriod,
            MonthlyPrice,
            Now);
        if (status == SubscriptionStatus.Pending)
            return subscription;

        Assert.Equal(BillingChangeResult.Applied, subscription.BindProviderReferences("sub-ref-1", "subscriber-ref-1", Now));
        var at = observedAtUtc ?? Now.AddMinutes(1);
        var path = status switch
        {
            SubscriptionStatus.Active => [SubscriptionStatus.Active],
            SubscriptionStatus.PastDue => new[] { SubscriptionStatus.Active, SubscriptionStatus.PastDue },
            SubscriptionStatus.Cancelled => [SubscriptionStatus.Active, SubscriptionStatus.Cancelled],
            SubscriptionStatus.Expired => [SubscriptionStatus.Active, SubscriptionStatus.Expired],
            SubscriptionStatus.ReconciliationRequired => [SubscriptionStatus.ReconciliationRequired],
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
        for (var i = 0; i < path.Length; i++)
            Assert.Equal(
                BillingChangeResult.Applied,
                subscription.ApplyProviderStatus(path[i], at.AddSeconds(i - path.Length + 1), Now));
        return subscription;
    }

    public static BillingProviderPayment Payment(string reference, DateTime periodStart, BillingPrice? price = null) =>
        new(reference, price ?? MonthlyPrice, periodStart, periodStart.AddMonths(1), periodStart.AddMinutes(5));
}

/// <summary>
/// An in-memory SQLite CentralDb created from the model (as the signup tests do). Each <see cref="Store"/> uses its own
/// context, like a separate request; SQLite does not prove SQL Server locking (see BillingSqlServerTests).
/// </summary>
internal sealed class SqliteCentralDatabase : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly List<CentralDbContext> _contexts = [];

    public SqliteCentralDatabase()
    {
        _connection.Open();
        using var db = Open();
        db.Database.EnsureCreated();
    }

    public BillingClock Time { get; } = new(BillingTestData.Now);

    public CentralDbContext Open(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        var db = new CentralDbContext(builder.Options);
        _contexts.Add(db);
        return db;
    }

    public BillingSubscriptionStore Store(params IInterceptor[] interceptors) =>
        new(Open(interceptors), Time, NullLogger<BillingSubscriptionStore>.Instance);

    public async Task<PendingRegistration> SeedRegistrationAsync(
        PendingRegistrationStatus status = PendingRegistrationStatus.AwaitingPayment,
        Guid? tenantId = null)
    {
        await using var db = Open();
        if (tenantId is { } id)
            db.Tenants.Add(BillingTestData.Tenant(id));
        var registration = BillingTestData.Registration(status, tenantId);
        db.PendingRegistrations.Add(registration);
        await db.SaveChangesAsync();
        return registration;
    }

    public void Dispose()
    {
        foreach (var db in _contexts)
            db.Dispose();
        _connection.Dispose();
    }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class BillingClock(DateTime utcNow) : TimeProvider
{
    public DateTime UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
}
