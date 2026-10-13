using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Realtime;

/// <summary>Records every notification; can inspect the database at that moment, or fail like a broken channel.</summary>
internal sealed class RecordingTenantChangeNotifier : ITenantChangeNotifier
{
    private readonly ConcurrentQueue<(Guid TenantId, TenantChange Change)> _sent = new();

    public Func<Guid, TenantChange, Task>? OnNotify { get; set; }

    public Exception? Throw { get; set; }

    public IReadOnlyList<(Guid TenantId, TenantChange Change)> Sent => _sent.ToArray();

    public IReadOnlyList<PrintJobChanged> PrintJobChanges => _sent.Select(s => s.Change).OfType<PrintJobChanged>().ToList();

    public async Task NotifyAsync(Guid tenantId, TenantChange change, CancellationToken ct)
    {
        _sent.Enqueue((tenantId, change));
        if (OnNotify is not null)
            await OnNotify(tenantId, change);
        if (Throw is not null)
            throw Throw;
    }
}

internal sealed class DefaultReceiptTemplates : IReceiptTemplateSettingsService
{
    public Task<ReceiptTemplateSettings> GetAsync(Guid customerId, string? customerDisplayName, string? defaultReceiptLanguage, CancellationToken ct) =>
        Task.FromResult(ReceiptTemplateSettings.CreateDefaults(customerDisplayName, defaultReceiptLanguage));

    public Task<ReceiptTemplateSettings> UpdateAsync(Guid customerId, string? customerDisplayName, UpdateReceiptTemplateSettingsCommand command, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>The print services over one file-backed SQLite database per tenant, with one notifier.</summary>
internal sealed class PrintServices
{
    public PrintServices(RecordingTenantDbFactory tenants, ITenantChangeNotifier notifier)
    {
        Tenants = tenants;
        Receipts = new ReceiptPrintJobService(tenants, new DefaultReceiptTemplates(), notifier, NullLogger<ReceiptPrintJobService>.Instance);
        Bridge = new PrintBridgeJobService(tenants, notifier, NullLogger<PrintBridgeJobService>.Instance);
        History = new PrintJobHistoryService(tenants, new DefaultReceiptTemplates(), notifier, NullLogger<PrintJobHistoryService>.Instance);
        Manual = new ManualOrderPrintService(tenants, Receipts, History, NullLogger<ManualOrderPrintService>.Instance);
    }

    public RecordingTenantDbFactory Tenants { get; }
    public ReceiptPrintJobService Receipts { get; }
    public PrintBridgeJobService Bridge { get; }
    public PrintJobHistoryService History { get; }
    public ManualOrderPrintService Manual { get; }

    public static Guid SeedOrder(RecordingTenantDbFactory tenants, Guid tenantId, string externalId = "ORDER-1")
    {
        var orderId = Guid.NewGuid();
        tenants.Seed(tenantId, db => db.Orders.Add(Order(orderId, tenantId, externalId)));
        return orderId;
    }

    public static Order Order(Guid orderId, Guid tenantId, string externalId) => new()
    {
        Id = orderId,
        Platform = FoodPlatform.TrendyolYemek,
        ExternalOrderId = externalId,
        ExternalOrderCode = $"TY-{externalId}",
        IdempotencyKey = $"TrendyolYemek:{externalId}:{tenantId:N}",
        InternalStatus = OrderStatus.Accepted,
        PlatformStatus = "Picking",
        CustomerName = "Test Customer",
        CustomerPhone = "+905555555555",
        CustomerAddress = "Test Address",
        TotalAmount = 100m,
        PaymentMethod = PaymentMethod.CreditCard,
        PaymentStatus = PaymentStatus.Paid,
        CreatedAtPlatform = DateTime.UtcNow.AddMinutes(-5),
        ReceivedAt = DateTime.UtcNow.AddMinutes(-4),
        RawPayloadJson = "{}",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Items =
        [
            new OrderItem
            {
                Id = Guid.NewGuid(),
                ProductName = "Lahmacun",
                Quantity = 1,
                UnitPrice = 100m,
                TotalPrice = 100m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        ]
    };

    public async Task<PrintJob> ReadJobAsync(Guid tenantId, Guid jobId)
    {
        await using var db = await Tenants.CreateAsync(tenantId, CancellationToken.None);
        return db.PrintJobs.Single(j => j.Id == jobId);
    }

    public async Task<IReadOnlyList<PrintJob>> ReadJobsAsync(Guid tenantId, Guid orderId)
    {
        await using var db = await Tenants.CreateAsync(tenantId, CancellationToken.None);
        return db.PrintJobs.Where(j => j.OrderId == orderId).OrderBy(j => j.StatusVersion).ToList();
    }
}
