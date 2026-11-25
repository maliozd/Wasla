using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// Decision R5(a) of WAS-232: the Trendyol GO client recognizes its package statuses itself, without the Orders status
/// mapper, and still warns about an unknown status at the same level, with the same message template and property, as
/// it did through the mapper. The warning is now written under the client's own logger category. Ingestion keeps its
/// own warning (<see cref="DefaultOrderStatusMapper"/>), so an unknown status is still logged twice.
/// </summary>
public sealed class TrendyolGoUnknownStatusWarningTests
{
    private const string Template = "Unknown Trendyol status '{ExternalStatus}', defaulting to New";

    /// <summary>Every status the mapper maps without a warning, a few spellings around them, and blank values.</summary>
    public static TheoryData<string?> Recognized => new(
        null, "", "   ",
        "Created", "Picking", "Invoiced", "Shipped", "Delivered", "Cancelled", "UnSupplied",
        "Returned", "RETURNED", "returned", " Picking ", "Created ", "Yeni ",
        "Yeni", "yeni", "new", "New",
        "Onaylandı", "Onaylandi", "onaylandı", "onaylandi",
        "Hazırlanıyor", "Hazirlaniyor", "hazırlanıyor", "hazirlaniyor",
        "Hazır", "Hazir", "hazır", "hazir",
        "Yolda", "yolda",
        "Teslim", "Teslim edildi", "teslim", "teslim edildi",
        "İptal", "Iptal", "İptal edildi", "Iptal edildi", "iptal", "iptal edildi",
        "Başarısız", "Basarisiz", "başarısız", "basarisiz");

    /// <summary>Statuses the mapper stores as New with a warning: other casings of known ones, and unknown text.</summary>
    public static TheoryData<string> Unknown => new(
        "created", "PICKING", "picking", "NEW", "Teslim Edildi", "Returned2", "Hazırlandı", "Unknown", " Mystery ");

    [Theory]
    [MemberData(nameof(Recognized))]
    public async Task ARecognizedOrBlankStatus_LogsNoWarning(string? status)
    {
        var log = await FetchAsync(status);

        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Empty(MapperWarnings(status));
    }

    [Theory]
    [MemberData(nameof(Unknown))]
    public async Task AnUnknownStatus_IsOneWarning_WithTheTemplateAndTheTrimmedStatus(string status)
    {
        var log = await FetchAsync(status);

        var warning = Assert.Single(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(Template, warning.Template);
        Assert.Equal(status.Trim(), warning.ExternalStatus);

        // Ingestion still warns about the same status with the same template.
        var ingestion = Assert.Single(MapperWarnings(status));
        Assert.Equal(LogLevel.Warning, ingestion.Level);
        Assert.Equal(Template, ingestion.Template);
        Assert.Equal(status.Trim(), ingestion.ExternalStatus);
    }

    private static List<(LogLevel Level, string? Template, string? ExternalStatus)> MapperWarnings(string? status)
    {
        var log = new TemplateLogger<DefaultOrderStatusMapper>();
        new DefaultOrderStatusMapper(log).MapToInternalStatus(FoodPlatform.TrendyolYemek, status ?? string.Empty);
        return log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => (e.Level, e.Template, e.ExternalStatus)).ToList();
    }

    private static async Task<TemplateLogger<TrendyolGoFoodPlatformClient>> FetchAsync(string? status)
    {
        var statusProperty = status is null ? string.Empty : $",\"packageStatus\":{JsonSerializer.Serialize(status)}";
        var page = $$"""{"page":0,"size":50,"totalPages":1,"totalCount":1,"content":[{"id":"p1","orderNumber":"p1","packageCreationDate":1710000000000{{statusProperty}},"totalPrice":10}]}""";
        var log = new TemplateLogger<TrendyolGoFoodPlatformClient>();
        var options = new TrendyolGoOptions { AgentName = "Wasla", BaseUrl = "https://trendyol.example/" };
        var client = new TrendyolGoFoodPlatformClient(
            new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page, Encoding.UTF8, "application/json")
            }))
            {
                BaseAddress = new Uri(options.BaseUrl)
            },
            Options.Create(options),
            Options.Create(new TrendyolGoMealWebhookPilotOptions()),
            log,
            new LimiterClock().Limiter(),
            TimeProvider.System);
        var connection = new PlatformConnection
        {
            Platform = FoodPlatform.TrendyolYemek,
            StoreId = "store-1",
            SupplierId = "supplier-1",
            ExecutorEmail = "executor@example.invalid",
            EncryptedApiKey = "key",
            EncryptedApiSecret = "secret",
            IsActive = true
        };
        var end = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);

        var orders = await client.FetchOrdersAsync(
            connection.AsProviderContext(),
            new OrderFetchWindow(end.AddHours(-1), end),
            TestContext.Current.CancellationToken);

        // The raw status reaches ingestion unchanged; the client maps nothing.
        Assert.Equal(status ?? string.Empty, Assert.Single(orders).ExternalStatus);
        return log;
    }

    /// <summary>Keeps each entry's level, message template and <c>ExternalStatus</c> property.</summary>
    private sealed class TemplateLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string? Template, string? ExternalStatus)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            string? Value(string name) => properties.FirstOrDefault(p => p.Key == name).Value?.ToString();
            Entries.Add((logLevel, Value("{OriginalFormat}"), Value("ExternalStatus")));
        }
    }
}
